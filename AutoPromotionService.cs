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
/// Nightly background service that automatically promotes members who have
/// met both the time-in-rank and activity thresholds for the RCT → CPL chain.
///
/// Activity is counted from the member's current rank assignment date (stored
/// in RankHistory.AssignedAt), falling back to guild-join date if no record exists.
///
/// Members with the AWOL role are skipped. Each run promotes eligible members
/// by at most one rank — backlogged users will catch up over subsequent nights.
///
/// Promotions can be disabled globally or put in DryRun mode via appsettings.
/// </summary>
public class AutoPromotionService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AutoPromotionService> _logger;
    private readonly BotConfig _config;
    private readonly PromotionService _promotion;

    /// <summary>
    /// The promotion tiers for auto-promotion (RCT → CPL).
    /// Matches the requirements from the clan's promotion doc.
    /// </summary>
    private static readonly AutoPromotionTier[] DefaultTiers =
    {
        new("RCT", "pvt", 7,  5,  1.0),
        new("PVT", "pfc", 7,  10, 2.0),
        new("PFC", "spc", 7,  10, 2.0),
        new("SPC", "cpl", 14, 25, 5.0),
    };

    public AutoPromotionService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<AutoPromotionService> logger,
        IOptions<BotConfig> config,
        PromotionService promotion)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
        _promotion = promotion;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.AutoPromotionEnabled)
        {
            _logger.LogInformation("AutoPromotionService is disabled via config. Exiting.");
            return;
        }

        // Wait until the Discord client is fully connected
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            _logger.LogInformation("Auto-promotion: waiting for Discord client to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        // Give guilds a moment to finish downloading members
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation(
            "Auto-promotion service started. Running daily at {Hour:D2}:00 UTC. DryRun={DryRun}",
            _config.AutoPromotionRunHourUtc, _config.AutoPromotionDryRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun();
            _logger.LogInformation(
                "Auto-promotion: next run in {Hours:F1} hours (at {NextRun:yyyy-MM-dd HH:mm} UTC)",
                delay.TotalHours, DateTime.UtcNow.Add(delay));

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await RunAutoPromotionAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during auto-promotion cycle");
            }
        }
    }

    /// <summary>
    /// Calculates how long to wait until the next scheduled run hour (UTC).
    /// If the hour has already passed today, schedules for the same hour tomorrow.
    /// </summary>
    private TimeSpan GetDelayUntilNextRun()
    {
        var now = DateTime.UtcNow;
        var runHour = Math.Clamp(_config.AutoPromotionRunHourUtc, 0, 23);

        var nextRun = new DateTime(now.Year, now.Month, now.Day, runHour, 0, 0, DateTimeKind.Utc);
        if (nextRun <= now)
            nextRun = nextRun.AddDays(1);

        return nextRun - now;
    }

    private async Task RunAutoPromotionAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting auto-promotion cycle at {Time} UTC (DryRun={DryRun})",
            DateTime.UtcNow, _config.AutoPromotionDryRun);

        foreach (var guild in _client.Guilds)
        {
            try
            {
                await ProcessGuildAsync(guild, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing guild {GuildName} ({GuildId}) for auto-promotion",
                    guild.Name, guild.Id);
            }
        }

        _logger.LogInformation("Auto-promotion cycle complete");
    }

    private async Task ProcessGuildAsync(SocketGuild guild, CancellationToken ct)
    {
        await guild.DownloadUsersAsync();

        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

        var tiers = DefaultTiers;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var evaluated = 0;
        var promoted = 0;
        var skipped = 0;

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;

            // Find the tier matching the member's current rank, if any
            var currentRank = GetCurrentRank(member);
            if (currentRank is null) continue; // No rank role — nothing to auto-promote

            var tier = tiers.FirstOrDefault(t =>
                t.FromRank.Equals(currentRank, StringComparison.OrdinalIgnoreCase));

            if (tier is null) continue; // Above CPL or unknown rank — skip

            evaluated++;

            // ── AWOL check ──────────────────────────────────────────
            if (awolRole is not null && member.Roles.Any(r => r.Id == awolRole.Id))
            {
                _logger.LogDebug("Auto-promo skip: {User} is AWOL", member.Username);
                skipped++;
                continue;
            }

            // ── Time-in-rank check ──────────────────────────────────
            var assignedAt = await _promotion.GetRankAssignedAtAsync(guild.Id, member, ct);
            if (assignedAt is null)
            {
                _logger.LogDebug("Auto-promo skip: {User} has no rank-assigned timestamp or join date",
                    member.Username);
                skipped++;
                continue;
            }

            var timeInRank = DateTime.UtcNow - assignedAt.Value;
            var requiredTime = TimeSpan.FromDays(tier.DaysInRank);
            if (timeInRank < requiredTime)
            {
                _logger.LogDebug(
                    "Auto-promo skip: {User} in {Rank} for {Days:F1}d, needs {Required}d",
                    member.Username, currentRank, timeInRank.TotalDays, tier.DaysInRank);
                skipped++;
                continue;
            }

            // ── Activity check (within current rank only) ───────────
            var messageCount = await db.MessageEvents
                .CountAsync(m => m.GuildId == guild.Id
                              && m.UserId == member.Id
                              && m.Timestamp >= assignedAt.Value, ct);

            var voiceSeconds = await GetVoiceSecondsSinceAsync(db, guild.Id, member.Id, assignedAt.Value, ct);
            var voiceHours = voiceSeconds / 3600.0;

            var meetsMessages = messageCount >= tier.MinMessages;
            var meetsVoice = voiceHours >= tier.MinVoiceHours;

            if (!meetsMessages && !meetsVoice)
            {
                _logger.LogDebug(
                    "Auto-promo skip: {User} activity below threshold — msgs={Msgs}/{MinMsgs}, voice={Voice:F1}h/{MinVoice}h",
                    member.Username, messageCount, tier.MinMessages, voiceHours, tier.MinVoiceHours);
                skipped++;
                continue;
            }

            // ── Eligible — promote ──────────────────────────────────
            _logger.LogInformation(
                "Auto-promo ELIGIBLE: {User} {From} → {To} (time={Days:F1}d, msgs={Msgs}, voice={Voice:F1}h)",
                member.Username, tier.FromRank, tier.ToRank.ToUpperInvariant(),
                timeInRank.TotalDays, messageCount, voiceHours);

            if (_config.AutoPromotionDryRun)
            {
                _logger.LogInformation("Auto-promo DRY RUN: would promote {User} to {NewRank}",
                    member.Username, tier.ToRank.ToUpperInvariant());
                continue;
            }

            var result = await _promotion.PromoteAsync(member, tier.ToRank);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "Auto-promo FAILED for {User} → {Rank}: {Error}",
                    member.Username, tier.ToRank, result.Error);
                continue;
            }

            promoted++;

            // Announce in the configured channel
            await _promotion.AnnouncePromotionAsync(
                guild,
                member,
                fromRankShort: tier.FromRank,
                toRankShort: result.NewRankName,
                channelName: _config.AutoPromotionAnnouncementChannel);
        }

        _logger.LogInformation(
            "Auto-promotion for guild {Guild}: evaluated={Evaluated}, promoted={Promoted}, skipped={Skipped}",
            guild.Name, evaluated, promoted, skipped);
    }

    /// <summary>
    /// Returns the member's highest-ranking rank role name, or null if they have none.
    /// Uses the RankRoles config list (ordered low → high) to determine ordering.
    /// </summary>
    private string? GetCurrentRank(SocketGuildUser member)
    {
        var rankRoles = _config.GetRankRolesList();
        var memberRoleNames = member.Roles.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Contains(rankRoles[i]))
                return rankRoles[i];
        }
        return null;
    }

    /// <summary>
    /// Calculates voice seconds logged since the given cutoff timestamp.
    /// Mirrors the logic in AwolCheckService.GetVoiceSecondsAsync to stay consistent,
    /// including overlapping sessions and currently-open sessions.
    /// </summary>
    private static async Task<long> GetVoiceSecondsSinceAsync(
        BotDbContext db, ulong guildId, ulong userId, DateTime cutoff, CancellationToken ct)
    {
        // Sessions that started after the cutoff
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.UserId == userId
                     && v.JoinedAt >= cutoff)
            .ToListAsync(ct);

        long totalSeconds = 0;
        foreach (var session in sessions)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            totalSeconds += (long)(end - session.JoinedAt).TotalSeconds;
        }

        // Sessions that started before the cutoff but are still open or ended after
        var overlapping = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.UserId == userId
                     && v.JoinedAt < cutoff
                     && (v.LeftAt == null || v.LeftAt > cutoff))
            .ToListAsync(ct);

        foreach (var session in overlapping)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            totalSeconds += (long)(end - cutoff).TotalSeconds;
        }

        return totalSeconds;
    }

    /// <summary>
    /// Represents a single auto-promotion tier.
    /// </summary>
    private record AutoPromotionTier(
        string FromRank,
        string ToRank,           // shorthand (lowercase) matching PromotionService.RankMap keys
        int DaysInRank,
        int MinMessages,
        double MinVoiceHours);
}