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
/// met both the time-in-rank and activity thresholds for the RCT → SGT chain.
///
/// Two activity models:
///   • RCT → CPL: message count OR voice hours since current rank assignment.
///   • CPL → SGT (and future ranks above): event attendance, sourced from the
///     EventAttendance table populated by EventAttendanceSnapshotService.
///
/// Activity is counted from the member's current rank assignment date (stored
/// in RankHistory.AssignedAt), falling back to guild-join date if no record exists.
///
/// Members with the AWOL role are skipped. Each run promotes eligible members
/// by at most one rank — backlogged users will catch up over subsequent nights.
///
/// Announcement posts are throttled via AutoPromotionAnnouncementDelaySeconds so
/// a cycle that promotes many members doesn't burst-post and trip Discord rate
/// limits or spam filters.
///
/// Promotions can be disabled globally (AutoPromotionDryRun) or per-tier via
/// AutoPromotionDryRunRanks — useful for rolling out a new tier alongside
/// existing live ones.
/// </summary>
public class AutoPromotionService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AutoPromotionService> _logger;
    private readonly BotConfig _config;
    private readonly PromotionService _promotion;

    /// <summary>
    /// The promotion tiers for auto-promotion (RCT → SGT).
    /// Matches the requirements from the clan's promotion doc.
    ///
    /// Tiers with MinEvents > 0 use event-attendance activity checks (via the
    /// EventAttendance table) and ignore MinMessages / MinVoiceHours.
    /// </summary>
    private static readonly AutoPromotionTier[] DefaultTiers =
    {
        new("RCT", "pvt", DaysInRank: 7,  MinMessages: 5,  MinVoiceHours: 1.0),
        new("PVT", "pfc", DaysInRank: 7,  MinMessages: 10, MinVoiceHours: 2.0),
        new("PFC", "spc", DaysInRank: 7,  MinMessages: 10, MinVoiceHours: 2.0),
        new("SPC", "cpl", DaysInRank: 14, MinMessages: 25, MinVoiceHours: 5.0),
        new("CPL", "sgt", DaysInRank: 14, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 2),
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

        var dryRunRanks = _config.GetAutoPromotionDryRunRanksList();
        _logger.LogInformation(
            "Auto-promotion service started. Running daily at {Hour:D2}:00 UTC. DryRun={DryRun}, DryRunRanks=[{DryRunRanks}], AnnouncementDelay={Delay}s",
            _config.AutoPromotionRunHourUtc,
            _config.AutoPromotionDryRun,
            string.Join(",", dryRunRanks),
            _config.AutoPromotionAnnouncementDelaySeconds);

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
        var dryRunRanks = _config.GetAutoPromotionDryRunRanksList();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var evaluated = 0;
        var promoted = 0;
        var skipped = 0;

        var pendingAnnouncements = new List<PendingAnnouncement>();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;

            var currentRank = GetCurrentRank(member);
            if (currentRank is null) continue;

            var tier = tiers.FirstOrDefault(t =>
                t.FromRank.Equals(currentRank, StringComparison.OrdinalIgnoreCase));

            if (tier is null) continue;

            evaluated++;

            if (awolRole is not null && member.Roles.Any(r => r.Id == awolRole.Id))
            {
                _logger.LogDebug("Auto-promo skip: {User} is AWOL", member.Username);
                skipped++;
                continue;
            }

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

            // ── Activity check ──────────────────────────────────────
            bool meetsActivity;
            string activityDetails;

            if (tier.MinEvents > 0)
            {
                var eventsAttended = await EventAttendanceHelper.CountEventsAttendedAsync(
                    db, guild.Id, member.Id, assignedAt.Value, ct);

                meetsActivity = eventsAttended >= tier.MinEvents;
                activityDetails = $"events={eventsAttended}/{tier.MinEvents}";
            }
            else
            {
                var messageCount = await db.MessageEvents
                    .CountAsync(m => m.GuildId == guild.Id
                                  && m.UserId == member.Id
                                  && m.Timestamp >= assignedAt.Value, ct);

                var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
                    db, guild.Id, member.Id, assignedAt.Value,
                    _config.MaxSingleSessionHours, ct);
                var voiceHours = voiceSeconds / 3600.0;

                var meetsMessages = messageCount >= tier.MinMessages;
                var meetsVoice    = voiceHours   >= tier.MinVoiceHours;

                meetsActivity = meetsMessages || meetsVoice;
                activityDetails =
                    $"msgs={messageCount}/{tier.MinMessages}, voice={voiceHours:F1}h/{tier.MinVoiceHours}h";
            }

            if (!meetsActivity)
            {
                _logger.LogDebug(
                    "Auto-promo skip: {User} activity below threshold — {Details}",
                    member.Username, activityDetails);
                skipped++;
                continue;
            }

            _logger.LogInformation(
                "Auto-promo ELIGIBLE: {User} {From} → {To} (time={Days:F1}d, {ActivityDetails})",
                member.Username, tier.FromRank, tier.ToRank.ToUpperInvariant(),
                timeInRank.TotalDays, activityDetails);

            var tierDryRun =
                dryRunRanks.Any(r => r.Equals(tier.FromRank, StringComparison.OrdinalIgnoreCase));

            if (_config.AutoPromotionDryRun || tierDryRun)
            {
                var reason = _config.AutoPromotionDryRun
                    ? "global DryRun"
                    : $"per-tier DryRun ({tier.FromRank})";
                _logger.LogInformation(
                    "Auto-promo DRY RUN ({Reason}): would promote {User} to {NewRank}",
                    reason, member.Username, tier.ToRank.ToUpperInvariant());
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

            pendingAnnouncements.Add(new PendingAnnouncement(
                Member: member,
                FromRankShort: tier.FromRank,
                ToRankShort: result.NewRankName));
        }

        _logger.LogInformation(
            "Auto-promotion for guild {Guild}: evaluated={Evaluated}, promoted={Promoted}, skipped={Skipped}",
            guild.Name, evaluated, promoted, skipped);

        if (pendingAnnouncements.Count > 0)
        {
            await AnnounceAllAsync(guild, pendingAnnouncements, ct);
        }
    }

    private async Task AnnounceAllAsync(
        SocketGuild guild,
        IReadOnlyList<PendingAnnouncement> announcements,
        CancellationToken ct)
    {
        string? channelName = _config.AutoPromotionAnnouncementChannel;
        if (_config.AutoPromotionAnnouncementChannelId != 0)
        {
            var channel = guild.GetTextChannel(_config.AutoPromotionAnnouncementChannelId);
            if (channel is null)
            {
                _logger.LogWarning(
                    "Auto-promotion announcement channel ID {ChannelId} not found in guild {GuildName}. Falling back to name '{ChannelName}'.",
                    _config.AutoPromotionAnnouncementChannelId, guild.Name, channelName);
            }
            else
            {
                channelName = channel.Name;
            }
        }

        var delayMs = Math.Max(0, _config.AutoPromotionAnnouncementDelaySeconds * 1000);

        _logger.LogInformation(
            "Posting {Count} promotion announcement(s) to #{ChannelName} with {Delay}s spacing",
            announcements.Count, channelName, _config.AutoPromotionAnnouncementDelaySeconds);

        for (int i = 0; i < announcements.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var a = announcements[i];
            await _promotion.AnnouncePromotionAsync(
                guild,
                a.Member,
                fromRankShort: a.FromRankShort,
                toRankShort: a.ToRankShort,
                channelName: channelName);

            if (i < announcements.Count - 1 && delayMs > 0)
            {
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException) { break; }
            }
        }
    }

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

    private record AutoPromotionTier(
        string FromRank,
        string ToRank,
        int DaysInRank,
        int MinMessages,
        double MinVoiceHours,
        int MinEvents = 0);

    private record PendingAnnouncement(
        SocketGuildUser Member,
        string FromRankShort,
        string ToRankShort);
}