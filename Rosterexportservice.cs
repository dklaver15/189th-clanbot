using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Background service that exports a full server roster to Google Sheets once per night.
/// Columns: Discord Name | Rank | All Roles | Join Date | Messages (window) |
///          Voice Hours (window) | AWOL Status | Time in Rank | Last Events VC
/// Also detects and persists rank changes for time-in-rank tracking.
/// </summary>
public class RosterExportService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly GoogleSheetsService _sheetsService;
    private readonly ILogger<RosterExportService> _logger;
    private readonly BotConfig _config;

    public RosterExportService(
        IServiceProvider services,
        DiscordSocketClient client,
        GoogleSheetsService sheetsService,
        ILogger<RosterExportService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _sheetsService = sheetsService;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be ready
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        _logger.LogInformation("RosterExportService started — will export at {Hour}:00 UTC daily",
            _config.RosterExportHourUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = now.Date.AddHours(_config.RosterExportHourUtc);
                if (nextRun <= now)
                    nextRun = nextRun.AddDays(1);

                var delay = nextRun - now;
                _logger.LogInformation("Next roster export at {NextRun} (in {Hours:F1}h)",
                    nextRun, delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                await RunExportAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Roster export failed — will retry next cycle");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    /// <summary>Builds the roster data and writes it to the Google Sheet.</summary>
    private async Task RunExportAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting nightly roster export...");

        var rankRoles = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        foreach (var guild in _client.Guilds)
        {
            // Download members to ensure the cache is fresh
            await guild.DownloadUsersAsync();

            var rows = new List<RosterRow>();

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            foreach (var member in guild.Users.Where(u => !u.IsBot))
            {
                var memberRoleNames = member.Roles
                    .Where(r => !r.IsEveryone)
                    .Select(r => r.Name)
                    .ToList();

                // Identify current rank (highest matching rank role)
                string? currentRank = null;
                for (int i = rankRoles.Count - 1; i >= 0; i--)
                {
                    if (memberRoleNames.Any(r => r.Equals(rankRoles[i], StringComparison.OrdinalIgnoreCase)))
                    {
                        currentRank = rankRoles[i];
                        break;
                    }
                }

                // Track / detect rank changes
                DateTime? rankSince = null;
                if (currentRank is not null)
                {
                    var rankRecord = await db.RankHistories
                        .FirstOrDefaultAsync(r => r.GuildId == guild.Id && r.UserId == member.Id, ct);

                    if (rankRecord is null)
                    {
                        // First time seeing this user — seed with join date as best approximation
                        rankRecord = new RankHistory
                        {
                            GuildId = guild.Id,
                            UserId = member.Id,
                            RankName = currentRank,
                            AssignedAt = member.JoinedAt?.UtcDateTime ?? now
                        };
                        db.RankHistories.Add(rankRecord);
                    }
                    else if (!rankRecord.RankName.Equals(currentRank, StringComparison.OrdinalIgnoreCase))
                    {
                        // Rank changed — update the record
                        _logger.LogInformation("Rank change detected: {User} {OldRank} → {NewRank}",
                            member.Username, rankRecord.RankName, currentRank);
                        rankRecord.RankName = currentRank;
                        rankRecord.AssignedAt = now;
                    }

                    rankSince = rankRecord.AssignedAt;
                }

                // Activity stats within roster window
                var windowStart = now.AddDays(-_config.RosterWindowDays);

                var messageCount = await db.MessageEvents
                    .CountAsync(m => m.GuildId == guild.Id
                                  && m.UserId == member.Id
                                  && m.Timestamp >= windowStart, ct);

                var voiceSeconds = await GetVoiceSecondsAsync(db, guild.Id, member.Id, windowStart, ct);
                var voiceHours = voiceSeconds / 3600.0;

                // AWOL status
                bool hasAwolRole = member.Roles.Any(r =>
                    r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

                // Last time in Events voice channel
                var lastEventsSession = await db.VoiceSessions
                    .Where(v => v.GuildId == guild.Id
                             && v.UserId == member.Id
                             && v.ChannelName != null
                             && v.ChannelName.Equals(_config.EventsVoiceChannelName))
                    .OrderByDescending(v => v.JoinedAt)
                    .FirstOrDefaultAsync(ct);

                // All roles (sorted, excluding @everyone)
                var rolesDisplay = string.Join(", ", member.Roles
                    .Where(r => !r.IsEveryone)
                    .OrderByDescending(r => r.Position)
                    .Select(r => r.Name));

                rows.Add(new RosterRow
                {
                    DiscordName = member.DisplayName,
                    Username = member.Username,
                    Rank = currentRank ?? "—",
                    Roles = rolesDisplay,
                    JoinDate = member.JoinedAt?.UtcDateTime,
                    Messages = messageCount,
                    VoiceHours = voiceHours,
                    WindowDays = _config.RosterWindowDays,
                    IsAwol = hasAwolRole,
                    RankSince = rankSince,
                    LastEventsVc = lastEventsSession?.JoinedAt
                });
            }

            await db.SaveChangesAsync(ct);

            // Sort by rank order (highest first), then alphabetically within rank
            rows = rows
                .OrderByDescending(r => r.Rank != "—" ? rankRoles.IndexOf(r.Rank) : -1)
                .ThenBy(r => r.DiscordName)
                .ToList();

            // Write to Google Sheets
            await _sheetsService.WriteRosterAsync(guild.Name, rows);

            _logger.LogInformation("Roster export complete for {Guild}: {Count} members", guild.Name, rows.Count);
        }
    }

    private static async Task<long> GetVoiceSecondsAsync(
        BotDbContext db, ulong guildId, ulong userId, DateTime windowStart, CancellationToken ct)
    {
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && v.JoinedAt >= windowStart)
            .ToListAsync(ct);

        long totalSeconds = 0;
        foreach (var session in sessions)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            var start = session.JoinedAt < windowStart ? windowStart : session.JoinedAt;
            totalSeconds += (long)(end - start).TotalSeconds;
        }

        var overlapping = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && v.JoinedAt < windowStart
                        && (v.LeftAt == null || v.LeftAt > windowStart))
            .ToListAsync(ct);

        foreach (var session in overlapping)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            totalSeconds += (long)(end - windowStart).TotalSeconds;
        }

        return totalSeconds;
    }
}

/// <summary>
/// DTO for a single roster row to pass to the Sheets service.
/// </summary>
public class RosterRow
{
    public string DiscordName { get; set; } = "";
    public string Username { get; set; } = "";
    public string Rank { get; set; } = "—";
    public string Roles { get; set; } = "";
    public DateTime? JoinDate { get; set; }
    public int Messages { get; set; }
    public double VoiceHours { get; set; }
    public int WindowDays { get; set; }
    public bool IsAwol { get; set; }
    public DateTime? RankSince { get; set; }
    public DateTime? LastEventsVc { get; set; }
}