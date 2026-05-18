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
///
/// ── How "Events VC" is matched ──
/// Both the "Last Events VC" column and the distinct-event attendance count
/// at current rank match against the EVENTS category (BotConfig.EventsCategoryId)
/// rather than a single channel. Any VC under that category — including temp
/// event VCs spun up for overflow or squad splits — counts. Sessions recorded
/// before CategoryId existed on VoiceSession (null CategoryId) fall back to
/// matching on EventsVoiceChannelId so historical rows still register.
///
/// ── Seeded events ──
/// "Events At Rank" includes both bot-tracked voice-session events since the
/// effective "since" date and any one-time seed applied via
/// /seed-promotion-credit. The effective "since" date is the later of the
/// rank-assigned date and the seed-applied date, so events before the seed
/// aren't double-counted.
///
/// ── Rank change detection ──
/// When a user's rank changes between exports, the RankHistory row is updated
/// with the new rank and timestamp. The seed fields
/// (EventsAttendedAtRankBeforeBot + SeedAppliedAt) are reset to (0, null) at
/// the same time — a new rank means a fresh event count, not an inherited
/// seed from the previous rank.
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

    /// <summary>Publicly callable entry point for manual exports (e.g. from /roster-export command).</summary>
    public Task RunManualExportAsync(CancellationToken ct = default) => RunExportAsync(ct);

    /// <summary>Builds the roster data and writes it to the Google Sheet.</summary>
    private async Task RunExportAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting roster export...");

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
                DateTime? seedAppliedAt = null;
                int seedEvents = 0;

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
                            AssignedAt = member.JoinedAt?.UtcDateTime ?? now,
                            EventsAttendedAtRankBeforeBot = 0,
                            SeedAppliedAt = null,
                        };
                        db.RankHistories.Add(rankRecord);
                    }
                    else if (!rankRecord.RankName.Equals(currentRank, StringComparison.OrdinalIgnoreCase))
                    {
                        // Rank changed — update the record and reset seed fields. The
                        // previous rank's seed is not inherited; promotion math at the
                        // new rank starts fresh.
                        _logger.LogInformation(
                            "Rank change detected: {User} {OldRank} → {NewRank} (clearing seed)",
                            member.Username, rankRecord.RankName, currentRank);

                        rankRecord.RankName = currentRank;
                        rankRecord.AssignedAt = now;
                        rankRecord.EventsAttendedAtRankBeforeBot = 0;
                        rankRecord.SeedAppliedAt = null;
                    }

                    rankSince = rankRecord.AssignedAt;
                    seedAppliedAt = rankRecord.SeedAppliedAt;
                    seedEvents = rankRecord.EventsAttendedAtRankBeforeBot;
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

                // Last time in any EVENTS-category voice channel (main Events VC
                // or any temp event VC under the category). Falls back to the
                // legacy channel-ID match for rows with null CategoryId.
                var categoryId = _config.EventsCategoryId;
                var legacyEventsChannelId = _config.EventsVoiceChannelId;

                var lastEventsSession = await db.VoiceSessions
                    .Where(v => v.GuildId == guild.Id
                             && v.UserId == member.Id
                             && (v.CategoryId == categoryId
                                 || (v.CategoryId == null && v.ChannelId == legacyEventsChannelId)))
                    .OrderByDescending(v => v.JoinedAt)
                    .FirstOrDefaultAsync(ct);

                // Events attendance count at current rank.
                // "Since" date is the later of AssignedAt and SeedAppliedAt so bot-tracked
                // events that happened before the seed are not double-counted (they're
                // assumed to already be reflected in the seed number). Final displayed
                // value = bot-tracked count + seed events.
                var eventsSince = rankSince ?? member.JoinedAt?.UtcDateTime ?? now;
                if (seedAppliedAt.HasValue && seedAppliedAt.Value > eventsSince)
                    eventsSince = seedAppliedAt.Value;

                var botTrackedEventsAtRank = await GetEventsAttendanceCountAsync(
                    db, guild.Id, member.Id,
                    categoryId,
                    legacyEventsChannelId,
                    eventsSince,
                    ct);

                var eventsAtRank = botTrackedEventsAtRank + seedEvents;

                // All roles (sorted, excluding @everyone)
                var rolesDisplay = string.Join(", ", member.Roles
                    .Where(r => !r.IsEveryone)
                    .OrderByDescending(r => r.Position)
                    .Select(r => r.Name));

                var isPromotable = true;
                if (voiceHours <= 5.0)
                {
                    isPromotable = messageCount >= 40;
                }
                else if (messageCount <= 10)
                {
                    isPromotable = voiceHours >= 10.0;
                } 

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
                    LastEventsVc = lastEventsSession?.JoinedAt,
                    EventsAtRank = eventsAtRank,
                    IsPromotable = isPromotable
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

            // Stamp BotState so /health can report the last successful export
            // without round-tripping to Sheets. SyncWithHandlers:
            // AutoPromotionService.GetOrCreateBotStateAsync — same singleton pattern.
            // Done in a fresh scope so the export's scoped DbContext (which has
            // held tracking state across hundreds of members) doesn't get reused
            // for what should be a single-row write.
            try
            {
                using var stateScope = _services.CreateScope();
                var stateDb = stateScope.ServiceProvider.GetRequiredService<BotDbContext>();
                var state = await stateDb.BotStates.FirstOrDefaultAsync(ct);
                if (state is null)
                {
                    state = new BotState();
                    stateDb.BotStates.Add(state);
                }
                state.LastRosterExportCompletedUtc = DateTime.UtcNow;
                await stateDb.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stamp BotState.LastRosterExportCompletedUtc");
            }
        }
    }

    /// <summary>
    /// Counts distinct EVENTS-category voice entries for a user since a given
    /// date, ignoring re-entries within 30 minutes of the previous session
    /// ending. Matches any VC under <paramref name="eventsCategoryId"/>, and
    /// falls back to <paramref name="legacyEventsChannelId"/> for sessions
    /// recorded before VoiceSession.CategoryId existed.
    /// </summary>
    private static async Task<int> GetEventsAttendanceCountAsync(
        BotDbContext db, ulong guildId, ulong userId,
        ulong eventsCategoryId, ulong legacyEventsChannelId,
        DateTime since, CancellationToken ct = default)
    {
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                        && v.UserId == userId
                        && (v.CategoryId == eventsCategoryId
                            || (v.CategoryId == null && v.ChannelId == legacyEventsChannelId))
                        && v.JoinedAt >= since)
            .OrderBy(v => v.JoinedAt)
            .Select(v => new { v.JoinedAt, v.LeftAt })
            .ToListAsync(ct);

        if (sessions.Count == 0)
            return 0;

        int count = 1; // First session always counts
        var lastSessionEnd = sessions[0].LeftAt ?? sessions[0].JoinedAt;

        for (int i = 1; i < sessions.Count; i++)
        {
            var gap = sessions[i].JoinedAt - lastSessionEnd;

            if (gap.TotalMinutes > 30)
            {
                count++;
            }

            // Always update the end marker to the latest session's end
            var thisEnd = sessions[i].LeftAt ?? sessions[i].JoinedAt;
            if (thisEnd > lastSessionEnd)
                lastSessionEnd = thisEnd;
        }

        return count;
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
    public int EventsAtRank { get; set; }
    public bool IsPromotable { get; set; }
}