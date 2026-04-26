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
/// met both the time-in-rank and activity thresholds for the RCT → CSM chain.
///
/// Two activity models:
///   • RCT → CPL: message count OR voice hours since current rank assignment.
///   • CPL → CSM (and any future ranks above): event attendance, sourced from
///     the EventAttendance table populated by EventAttendanceSnapshotService,
///     plus any one-time spreadsheet seed applied via /seed-promotion-credit.
///
/// Activity is counted from the member's current rank assignment date (stored
/// in RankHistory.AssignedAt), falling back to guild-join date if no record
/// exists. For event-based tiers, seeded events from the transition
/// spreadsheet are added on top, and bot-tracked events are only counted
/// from the seed-applied moment forward to avoid double-counting.
///
/// Members with the AWOL role are skipped. Each run promotes eligible members
/// by at most one rank — backlogged users will catch up over subsequent nights.
/// CSM → SMA is intentionally NOT in the ladder; SMA is a singular position
/// and should be assigned manually via /promote sma.
///
/// Announcement posts are throttled via AutoPromotionAnnouncementDelaySeconds so
/// a cycle that promotes many members doesn't burst-post and trip Discord rate
/// limits or spam filters.
///
/// Promotions can be disabled globally (AutoPromotionDryRun) or per-tier via
/// AutoPromotionDryRunRanks — useful for rolling out new tiers alongside
/// existing live ones.
///
/// ── Deploy-during-cycle catch-up ──
/// If the bot is redeployed during the configured run hour (or shortly
/// after), the standard scheduling path would silently skip today's cycle:
/// `nextRun = today's run hour` is in the past, the code pushes it to
/// tomorrow, and today's eligible members wait an extra day.
///
/// To prevent that, on startup we check BotState.LastAutoPromotionCompletedUtc.
/// If the most recent run hour is in the past AND no run has been recorded
/// after that run hour, we run a catch-up immediately. This is idempotent:
/// the catch-up itself stamps LastAutoPromotionCompletedUtc, so a second
/// restart on the same day after the catch-up sees the timestamp is current
/// and skips.
/// </summary>
public class AutoPromotionService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<AutoPromotionService> _logger;
    private readonly BotConfig _config;
    private readonly PromotionService _promotion;

    /// <summary>
    /// The promotion tiers for auto-promotion (RCT → CSM).
    ///
    /// ── RCT → CPL: message/voice activity ──
    /// Tiers with MinEvents == 0 use the legacy msg-OR-voice activity check.
    /// MinMessages and MinVoiceHours are checked with OR semantics: meeting
    /// either threshold qualifies the user.
    ///
    /// ── CPL → CSM: event-attendance activity ──
    /// Tiers with MinEvents > 0 use the EventAttendance table (plus any
    /// applied seed) and ignore MinMessages / MinVoiceHours entirely. The
    /// 2/3/4-point structure mirrors the clan's promotion doc.
    ///
    /// ── Linear progression notes ──
    /// MSG → 1SG → SGM is intentionally a linear chain. There is no
    /// MSG → SGM shortcut in PromotionService.RankMap, so the bot routes
    /// MSG holders through 1SG first; the next cycle that meets 1SG → SGM
    /// criteria carries them up. SMA is intentionally absent here — it's
    /// a singular position assigned manually.
    /// </summary>
    private static readonly AutoPromotionTier[] DefaultTiers =
    {
        // RCT → CPL: msg/voice activity
        new("RCT", "pvt", DaysInRank: 7,  MinMessages: 5,  MinVoiceHours: 1.0),
        new("PVT", "pfc", DaysInRank: 7,  MinMessages: 10, MinVoiceHours: 2.0),
        new("PFC", "spc", DaysInRank: 7,  MinMessages: 10, MinVoiceHours: 2.0),
        new("SPC", "cpl", DaysInRank: 14, MinMessages: 25, MinVoiceHours: 5.0),

        // CPL → CSM: event-attendance activity (DaysInRank converted from
        // weeks per the clan promotion doc; MinEvents = required points).
        new("CPL", "sgt", DaysInRank: 14, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 2),
        new("SGT", "ssg", DaysInRank: 21, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 2),
        new("SSG", "sfc", DaysInRank: 28, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 2),
        new("SFC", "msg", DaysInRank: 35, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 3),
        new("MSG", "1sg", DaysInRank: 35, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 3),
        new("1SG", "sgm", DaysInRank: 42, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 3),
        new("SGM", "csm", DaysInRank: 49, MinMessages: 0,  MinVoiceHours: 0,   MinEvents: 4),
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

        // ── Catch-up check ──
        // If we're past today's run hour and no run has been recorded for
        // today's window, run a catch-up immediately. This handles the case
        // where the bot was redeployed at or shortly after the run hour and
        // would otherwise have silently skipped today's cycle.
        try
        {
            await RunCatchUpIfNeededAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during auto-promotion catch-up check");
        }

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
    /// On startup, decides whether to run an immediate catch-up cycle to
    /// recover from a deploy that crossed the configured run hour.
    ///
    /// ── Decision logic ──
    /// Compute todayRunHourUtc = today's date at AutoPromotionRunHourUtc.
    /// If now &lt; todayRunHourUtc, today's run hasn't happened yet — normal
    /// scheduling will fire it on time, no catch-up needed.
    ///
    /// If now &gt;= todayRunHourUtc, today's run hour has already arrived. We
    /// need to know whether a cycle has actually completed since then. Read
    /// BotState.LastAutoPromotionCompletedUtc:
    ///   • null → fresh DB or first deploy after this feature shipped, run
    ///     a catch-up. This produces one extra cycle on the very first
    ///     deploy after the schema migration; the cycle is idempotent
    ///     (already-promoted users are no longer eligible) so this is
    ///     harmless.
    ///   • &lt; todayRunHourUtc → most recent recorded run was before today's
    ///     run hour, meaning today's run got skipped. Run a catch-up.
    ///   • &gt;= todayRunHourUtc → today's run already completed. Skip
    ///     catch-up; the main loop will schedule tomorrow's run normally.
    ///
    /// ── Idempotency ──
    /// RunAutoPromotionAsync stamps LastAutoPromotionCompletedUtc on
    /// success. A second restart on the same day will see the timestamp is
    /// after todayRunHourUtc and skip the catch-up. A user who got promoted
    /// in the catch-up cycle will not be eligible again in the second
    /// catch-up anyway (their RankHistory.AssignedAt got reset to "now"
    /// when promoted), so even if catch-up did re-run it would be a no-op
    /// for that user.
    /// </summary>
    private async Task RunCatchUpIfNeededAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var runHour = Math.Clamp(_config.AutoPromotionRunHourUtc, 0, 23);
        var todayRunHourUtc = new DateTime(now.Year, now.Month, now.Day, runHour, 0, 0, DateTimeKind.Utc);

        if (now < todayRunHourUtc)
        {
            _logger.LogInformation(
                "Auto-promotion: today's run hour ({RunHour:HH:mm} UTC) is still in the future; no catch-up needed",
                todayRunHourUtc);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var state = await GetOrCreateBotStateAsync(db, ct);
        var lastRun = state.LastAutoPromotionCompletedUtc;

        if (lastRun.HasValue && lastRun.Value >= todayRunHourUtc)
        {
            _logger.LogInformation(
                "Auto-promotion: today's run already completed at {LastRun:yyyy-MM-dd HH:mm} UTC; no catch-up needed",
                lastRun.Value);
            return;
        }

        _logger.LogInformation(
            "Auto-promotion: catch-up triggered. Today's run hour ({RunHour:HH:mm} UTC) has passed and the most recent recorded run was {LastRun}.",
            todayRunHourUtc,
            lastRun.HasValue ? lastRun.Value.ToString("yyyy-MM-dd HH:mm UTC") : "never");

        await RunAutoPromotionAsync(ct);
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

        // Stamp the completion timestamp regardless of dry-run mode. The cycle
        // itself ran; whether it produced real promotions doesn't change
        // whether we should consider today's slot "consumed". Skipping the
        // stamp on dry-run cycles would cause every dry-run deploy to
        // re-trigger catch-up, adding noise without value.
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateBotStateAsync(db, ct);
            state.LastAutoPromotionCompletedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Don't fail the whole cycle if state-stamping fails. The cost is
            // an extra catch-up on the next restart, which is harmless.
            _logger.LogWarning(ex, "Failed to stamp BotState.LastAutoPromotionCompletedUtc");
        }

        _logger.LogInformation("Auto-promotion cycle complete");
    }

    /// <summary>
    /// Loads the singleton BotState row, creating it if missing. Always
    /// returns a tracked entity so callers can mutate fields and SaveChanges.
    /// </summary>
    private static async Task<BotState> GetOrCreateBotStateAsync(BotDbContext db, CancellationToken ct)
    {
        var state = await db.BotStates.FirstOrDefaultAsync(ct);
        if (state is null)
        {
            state = new BotState();
            db.BotStates.Add(state);
            await db.SaveChangesAsync(ct);
        }
        return state;
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

            // Pull full rank info (assigned-at + seed fields) in one call so
            // event tiers can do seed-aware counting.
            var rankInfo = await _promotion.GetRankInfoAsync(guild.Id, member, ct);
            if (rankInfo.AssignedAt is null)
            {
                _logger.LogDebug("Auto-promo skip: {User} has no rank-assigned timestamp or join date",
                    member.Username);
                skipped++;
                continue;
            }

            var timeInRank = DateTime.UtcNow - rankInfo.AssignedAt.Value;
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
                    db, guild.Id, member.Id,
                    rankInfo.AssignedAt.Value,
                    rankInfo.SeedAppliedAt,
                    rankInfo.SeedEvents,
                    ct);

                meetsActivity = eventsAttended >= tier.MinEvents;

                // Surface seed contribution in the log so it's obvious when a
                // promotion decision leans on the one-time spreadsheet backfill.
                activityDetails = rankInfo.SeedEvents > 0
                    ? $"events={eventsAttended}/{tier.MinEvents} (seed={rankInfo.SeedEvents})"
                    : $"events={eventsAttended}/{tier.MinEvents}";
            }
            else
            {
                var messageCount = await db.MessageEvents
                    .CountAsync(m => m.GuildId == guild.Id
                                  && m.UserId == member.Id
                                  && m.Timestamp >= rankInfo.AssignedAt.Value, ct);

                var voiceSeconds = await VoiceActivityHelper.GetVoiceSecondsAsync(
                    db, guild.Id, member.Id, rankInfo.AssignedAt.Value,
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