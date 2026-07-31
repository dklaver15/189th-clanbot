using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Briefing;

public interface IBriefingDataCollector
{
    Task<BriefingContext> CollectAsync(
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default);
}

/// <summary>
/// Aggregates and caps the weekly snapshot. THIS is where the cost optimization
/// lives — anything that could grow with roster size gets filtered/sorted/Take(N)
/// here. The prompt should never see raw EF Core query results.
///
/// ── v2 enrichments ──
/// Each AwolRiskItem and PromotionCandidate now carries activity context
/// (messages + voice hours) so Claude can differentiate "fully checked out"
/// from "borderline" and "ready and active" from "ready by time only".
///
/// New sections:
///   • RiskWatch — members 50–99% of activity threshold (not yet AWOL).
///     Lets officers re-engage proactively before AwolCheckService flags them.
///   • Spotlight — single most-active member of the week by weighted score.
///   • WeekOverWeek — deltas vs the prior 7-day window for top-line numbers.
///
/// ── v3 enrichments ──
///   • RecruitmentSources + TopReferrers — joins this week broken down by
///     labeled invite source, plus top member referrers. Sourced from
///     InviteJoin via BriefingInviteSection. Independent of all other
///     sections; lives in its own helper class for clarity.
///
/// ── Performance: batch activity loading ──
/// Per-member message/voice queries scale with roster size (126+ active
/// members today). Two GROUP BY queries replace 2N per-member queries:
/// LoadBatchActivityAsync returns Dictionary&lt;UserId, (msgs, voiceSec)&gt;
/// for any window. Voice math approximates the per-session cap by clamping
/// each (LeftAt - JoinedAt) span in-memory; matches VoiceActivityHelper's
/// behaviour for the briefing's purposes.
///
/// ── Per-section error isolation ──
/// Each Get*Async block runs through SafeAsync so one failing data source
/// doesn't kill the whole briefing. Failures are logged; that section returns
/// empty.
/// </summary>
public sealed class BriefingDataCollector : IBriefingDataCollector
{
    /// <summary>
    /// Time-in-rank thresholds, kept in sync with AutoPromotionService.DefaultTiers.
    /// Briefing only checks the time gate; activity is enforced at promotion time.
    /// </summary>
    private static readonly Dictionary<string, (string ToRank, int DaysInRank)> PromotionTiers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["RCT"] = ("PVT", 7),
            ["PVT"] = ("PFC", 7),
            ["PFC"] = ("SPC", 7),
            ["SPC"] = ("CPL", 14),
            ["CPL"] = ("SGT", 14),
            ["SGT"] = ("SSG", 21),
            ["SSG"] = ("SFC", 28),
            ["SFC"] = ("MSG", 35),
            ["MSG"] = ("1SG", 35),
            ["1SG"] = ("SGM", 42),
            ["SGM"] = ("CSM", 49),
        };

    // Caps — these bound input token cost. Each bullet is roughly 30–70 input tokens.
    private const int MaxAwolRisks = 8;
    private const int MaxPromotionCandidates = 8;
    private const int MaxNotableEvents = 5;
    private const int MaxRiskWatch = 5;

    // Activity windows
    private const int ActivityContextWindowDays = 14;  // window for AWOL-item context
    private const int SpotlightWindowDays = 7;          // "this week" for the spotlight pick

    // Spotlight scoring weights — tune to taste. Events are weighted highest
    // because they're scarce and high-effort; voice is mid (sustained presence);
    // messages are baseline (cheap signal).
    private const int ScorePerMessage    = 1;
    private const int ScorePerVoiceHour  = 5;
    private const int ScorePerEvent      = 10;

    // Risk watch threshold band — members at [Lower, Upper) of the activity
    // threshold on at least one axis surface here. Below Lower they're about
    // to be flagged AWOL anyway; at/above Upper they're safe.
    private const double RiskWatchLowerRatio = 0.50;
    private const double RiskWatchUpperRatio = 1.00;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<BriefingDataCollector> _logger;

    public BriefingDataCollector(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<BriefingDataCollector> logger)
    {
        _services = services;
        _client = client;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<BriefingContext> CollectAsync(
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Collecting briefing data for {Start:yyyy-MM-dd} → {End:yyyy-MM-dd}",
            weekStart, weekEnd);

        var guild = _client.Guilds.FirstOrDefault();
        if (guild is null)
        {
            _logger.LogWarning("BriefingDataCollector: no Discord guild connected — returning empty context");
            return EmptyContext(weekStart, weekEnd);
        }
        if (_client.Guilds.Count > 1)
        {
            _logger.LogWarning(
                "BriefingDataCollector: bot is in {Count} guilds; only operating on '{First}'",
                _client.Guilds.Count, guild.Name);
        }

        await guild.DownloadUsersAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;

        // ── Batch-load activity for the windows we need ──
        // Two queries each — GROUP BY messages, raw voice sessions for in-memory aggregation.
        var ctx14d = await LoadBatchActivityAsync(db, guild.Id, now.AddDays(-ActivityContextWindowDays), now, ct);
        var ctx7d  = await LoadBatchActivityAsync(db, guild.Id, now.AddDays(-SpotlightWindowDays), now, ct);

        // Risk Watch must measure each member over their *own* AWOL window
        // (WindowDays for ranked members, ShortWindowDays for Guest, RCT and
        // anyone with no rank role) so its numbers mean the same thing
        // AwolCheckService's do. Using a single fixed
        // window here previously produced lines like "4/5 msgs over 28d" that
        // were actually 14d counts — a member with 300+ messages spread across
        // the month could surface as at-risk. GetWindowDaysForRoles only ever
        // returns one of these two values, so this is at most two extra loads.
        var riskActivityByWindow = new Dictionary<int, BatchActivity>();
        foreach (var days in new[] { _config.WindowDays, _config.ShortWindowDays }
                     .Where(d => d > 0)
                     .Distinct())
        {
            riskActivityByWindow[days] =
                await LoadBatchActivityAsync(db, guild.Id, now.AddDays(-days), now, ct);
        }

        // ── Run independent fetches concurrently, each isolated from failures ──
        var awolTask     = SafeAsync(() => GetAwolRisksAsync(guild, db, ctx14d, ct), "AWOL risks", []);
        var promoTask    = SafeAsync(() => GetPromotionCandidatesAsync(guild, db, ct), "promotion candidates", []);
        var eventsTask   = SafeAsync(() => GetNotableEventsAsync(guild, db, weekStart, weekEnd, ct), "notable events", []);
        var topLineTask  = SafeAsync(
            () => GetTopLineNumbersAsync(guild, db, weekStart, weekEnd, ct),
            "top-line numbers",
            new TopLineNumbers(0, 0, 0d, 0));
        var priorTopLineTask = SafeAsync(
            () => GetTopLineNumbersAsync(guild, db, weekStart.AddDays(-7), weekStart, ct),
            "prior-week top-line",
            new TopLineNumbers(0, 0, 0d, 0));
        var spotlightTask = SafeAsync(() => GetSpotlightAsync(guild, db, ctx7d, ct), "spotlight", null);
        var riskTask      = SafeAsync(() => GetRiskWatchAsync(guild, riskActivityByWindow, ct), "risk watch", []);

        // ── v3: Recruitment sources + top referrers ──
        // Lives in BriefingInviteSection so the new section doesn't tangle
        // with the existing collector logic. Empty fallback covers both
        // sub-results in one shot — same pattern as the topLine fallback above.
        var inviteTask = SafeAsync(
            () => BriefingInviteSection.CollectAsync(guild, db, weekStart, weekEnd, ct),
            "recruitment sources",
            (Sources: (IReadOnlyList<RecruitmentSourceItem>)Array.Empty<RecruitmentSourceItem>(),
             Referrers: (IReadOnlyList<TopReferrerItem>)Array.Empty<TopReferrerItem>()));

        // ── Recruit leads (Reddit RSS-sourced funnel) ──
        // Same external-helper pattern as inviteTask. Returns null when the
        // briefing window saw zero leads — the prompt treats null as "skip
        // the section" so disabled-feature and quiet-week look identical.
        var redditLeadsTask = SafeAsync<RedditLeadsSnapshot?>(
            () => BriefingRedditLeadsSection.CollectAsync(db, weekStart, weekEnd, ct),
            "recruit leads",
            null);

        // ── Retention (member departures) ──
        // Same external-helper + null-means-skip pattern. Gated on the feature
        // flag so the section can be hidden while the prompt is tuned without
        // affecting capture. NewRecruits isn't known until topLine resolves, so
        // we pass 0 here and correct NetMembershipChange after the WhenAll.
        var retentionTask = _config.RetentionSectionEnabled
            ? SafeAsync<RetentionSnapshot?>(
                () => BriefingRetentionSection.CollectAsync(
                    db, guild.Id, weekStart, weekEnd, newRecruitsThisWeek: 0, ct),
                "retention",
                null)
            : Task.FromResult<RetentionSnapshot?>(null);

        // ── Poll participation ──
        // Same external-helper + null-means-skip pattern. Null when no polls were
        // created this week and none are open.
        var pollsTask = SafeAsync<PollsSnapshot?>(
            () => BriefingPollSection.CollectAsync(
                db, guild, weekStart, weekEnd, _config.GetExemptRolesList(), _config.AwolRoleName, ct),
            "polls",
            null);

        await Task.WhenAll(
            awolTask, promoTask, eventsTask, topLineTask,
            priorTopLineTask, spotlightTask, riskTask,
            inviteTask, redditLeadsTask, retentionTask, pollsTask);

        var awolRisks = (await awolTask)
            .OrderByDescending(r => r.DaysSinceAwolAssigned)
            .Take(MaxAwolRisks)
            .ToList();

        var promotionCandidates = (await promoTask)
            .OrderByDescending(c => c.DaysInRank)
            .Take(MaxPromotionCandidates)
            .ToList();

        var notableEvents = (await eventsTask)
            .OrderByDescending(e => e.Attendance)
            .Take(MaxNotableEvents)
            .ToList();

        var riskWatch = (await riskTask)
            .OrderByDescending(r => r.ClosestThresholdRatio)
            .Take(MaxRiskWatch)
            .ToList();

        var topLine = await topLineTask;
        var priorTopLine = await priorTopLineTask;
        var spotlight = await spotlightTask;
        var (recruitmentSources, topReferrers) = await inviteTask;
        var redditLeads = await redditLeadsTask;
        var polls = await pollsTask;

        // NetMembershipChange needs the week's NewRecruits, only known now that
        // topLine has resolved. Correct the placeholder via a record `with`.
        var retention = await retentionTask;
        if (retention is not null)
        {
            retention = retention with
            {
                NetMembershipChange = topLine.NewRecruits - retention.TotalDepartures
            };
        }

        var deltas = new WeekOverWeekDeltas(
            EventsHeldDelta: topLine.EventsHeld - priorTopLine.EventsHeld,
            AverageAttendancePercentDelta: Math.Round(
                topLine.AverageAttendancePercent - priorTopLine.AverageAttendancePercent, 1),
            NewRecruitsDelta: topLine.NewRecruits - priorTopLine.NewRecruits);

        // ── Anomaly detection ──
        // Pre-computes factual signals (events drop, attendance swing, recruit
        // surge/drought, single-day join clustering) that the briefing prompt
        // is instructed to surface in the Notable section. Deterministic +
        // single-pass; see BriefingAnomalyDetector for rationale.
        //
        // The joins-by-day input is the only new query — bounded to a week's
        // worth of InviteJoin rows (dozens at most), so we pull timestamps
        // and bucket in memory instead of GROUP BY-ing in EF (avoids relying
        // on EF Core's SQLite date-translation, which can be brittle).
        var joinsByDay = await SafeAsync(async () =>
        {
            var timestamps = await db.InviteJoins
                .Where(j => j.GuildId == guild.Id
                         && j.JoinedAt >= weekStart
                         && j.JoinedAt < weekEnd)
                .Select(j => j.JoinedAt)
                .ToListAsync(ct);
            return (IReadOnlyDictionary<DateTime, int>)timestamps
                .GroupBy(t => t.Date)
                .ToDictionary(g => g.Key, g => g.Count());
        }, "joins-by-day", new Dictionary<DateTime, int>());

        var anomalies = BriefingAnomalyDetector.Detect(
            deltas,
            eventsHeldThisWeek: topLine.EventsHeld,
            averageAttendancePercentThisWeek: topLine.AverageAttendancePercent,
            newRecruitsThisWeek: topLine.NewRecruits,
            joinsByDay: joinsByDay);

        var context = new BriefingContext
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            ActiveMemberCount = topLine.ActiveMemberCount,
            EventsHeld = topLine.EventsHeld,
            AverageAttendancePercent = topLine.AverageAttendancePercent,
            NewRecruits = topLine.NewRecruits,
            WeekOverWeek = deltas,
            AwolRisks = awolRisks,
            PromotionCandidates = promotionCandidates,
            NotableEvents = notableEvents,
            RiskWatch = riskWatch,
            Spotlight = spotlight,
            RecruitmentSources = recruitmentSources,
            TopReferrers = topReferrers,
            RedditLeads = redditLeads,
            Retention = retention,
            Polls = polls,
            Anomalies = anomalies
        };

        _logger.LogInformation(
            "Briefing context: {Awol} AWOL risks, {Promo} promo candidates, {Events} events, " +
            "{Risk} risk-watch, {Sources} sources, {Referrers} referrers, " +
            "{Leads} recruit leads (stale claimed: {Stale}), " +
            "{Departures} departures (net {Net}), " +
            "spotlight={Spot}, {Members} active members, " +
            "WoW: events Δ{EventsDelta}, attendance Δ{AttendanceDelta}pp, recruits Δ{RecruitsDelta}, " +
            "{Anomalies} anomalies",
            awolRisks.Count, promotionCandidates.Count, notableEvents.Count,
            riskWatch.Count, recruitmentSources.Count, topReferrers.Count,
            redditLeads?.TotalSurfaced ?? 0, redditLeads?.StaleClaimedAllTime ?? 0,
            retention?.TotalDepartures ?? 0, retention?.NetMembershipChange ?? 0,
            spotlight?.Gamertag ?? "—", topLine.ActiveMemberCount,
            deltas.EventsHeldDelta, deltas.AverageAttendancePercentDelta, deltas.NewRecruitsDelta,
            anomalies.Count);

        return context;
    }

    // ── Batch activity loader ───────────────────────────────────────────
    // Two GROUP BY queries replace 2N per-member queries. Voice math
    // approximates VoiceActivityHelper's per-session cap by clamping each
    // (LeftAt - JoinedAt) span in-memory.
    private async Task<BatchActivity> LoadBatchActivityAsync(
        BotDbContext db, ulong guildId, DateTime since, DateTime until, CancellationToken ct)
    {
        var messageCounts = await db.MessageEvents
            .Where(m => m.GuildId == guildId
                     && m.Timestamp >= since
                     && m.Timestamp <= until)
            .GroupBy(m => m.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        // Pull raw closed sessions for the window, aggregate in-memory so we
        // can apply the per-session cap. Bounded by ~weeks of session history
        // — well under 10K rows for a normal clan.
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.LeftAt != null
                     && v.JoinedAt < until
                     && v.LeftAt >= since)
            .Select(v => new { v.UserId, v.JoinedAt, v.LeftAt })
            .ToListAsync(ct);

        var capSeconds = _config.MaxSingleSessionHours * 3600;

        var voiceSeconds = sessions
            .GroupBy(s => s.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(s =>
                {
                    // Clip to the window so partial overlaps don't over-count
                    var start = s.JoinedAt > since ? s.JoinedAt : since;
                    var end   = s.LeftAt!.Value < until ? s.LeftAt.Value : until;
                    var span  = (end - start).TotalSeconds;
                    if (span <= 0) return 0d;
                    return Math.Min(span, capSeconds);
                }));

        return new BatchActivity(messageCounts, voiceSeconds);
    }

    // ── AWOL Risks ──────────────────────────────────────────────────────
    private Task<List<AwolRiskItem>> GetAwolRisksAsync(
        SocketGuild guild,
        BotDbContext db,
        BatchActivity ctx14d,
        CancellationToken ct) =>
        GetAwolRisksImplAsync(guild, db, ctx14d, ct);

    private async Task<List<AwolRiskItem>> GetAwolRisksImplAsync(
        SocketGuild guild,
        BotDbContext db,
        BatchActivity ctx14d,
        CancellationToken ct)
    {
        var pending = await db.AwolRecords
            .Where(r => r.GuildId == guild.Id && !r.NotificationSent)
            .OrderBy(r => r.AssignedAt)
            .ToListAsync(ct);

        var rankRoles = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        var risks = new List<AwolRiskItem>();
        foreach (var record in pending)
        {
            var member = guild.GetUser(record.UserId);
            var daysSince = (int)(now - record.AssignedAt).TotalDays;

            var msgs14d = ctx14d.MessageCounts.GetValueOrDefault(record.UserId, 0);
            var voice14d = ctx14d.VoiceSeconds.GetValueOrDefault(record.UserId, 0d) / 3600.0;

            string currentRank;
            string trendNote;

            if (member is null)
            {
                currentRank = "Left Server";
                trendNote = $"flagged {daysSince}d ago — no longer in guild";
            }
            else
            {
                currentRank = GetCurrentRank(member, rankRoles) ?? "No Rank";
                trendNote = daysSince >= _config.AwolGraceDays
                    ? $"flagged {daysSince}d ago, grace period elapsed — ready for officer review"
                    : $"flagged {daysSince}d ago, in {_config.AwolGraceDays}-day grace period";
            }

            risks.Add(new AwolRiskItem(
                Gamertag: member?.DisplayName ?? record.Username,
                CurrentRank: currentRank,
                DaysSinceAwolAssigned: daysSince,
                Messages14d: msgs14d,
                VoiceHours14d: Math.Round(voice14d, 1),
                TrendNote: trendNote));
        }

        return risks;
    }

    // ── Promotion Candidates ────────────────────────────────────────────
    private async Task<List<PromotionCandidate>> GetPromotionCandidatesAsync(
        SocketGuild guild,
        BotDbContext db,
        CancellationToken ct)
    {
        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        var rankRoles = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        var rankHistories = await db.RankHistories
            .Where(r => r.GuildId == guild.Id)
            .ToDictionaryAsync(r => r.UserId, ct);

        var candidates = new List<PromotionCandidate>();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (awolRole is not null && member.Roles.Any(r => r.Id == awolRole.Id)) continue;

            var currentRank = GetCurrentRank(member, rankRoles);
            if (currentRank is null) continue;
            if (!PromotionTiers.TryGetValue(currentRank, out var tier)) continue;

            DateTime assignedAt;
            RankHistory? rh = null;
            if (rankHistories.TryGetValue(member.Id, out rh))
                assignedAt = rh.AssignedAt;
            else if (member.JoinedAt.HasValue)
                assignedAt = member.JoinedAt.Value.UtcDateTime;
            else
                continue;

            var daysInRank = (int)(now - assignedAt).TotalDays;
            if (daysInRank < tier.DaysInRank) continue;

            // Cumulative events at rank — same helper AutoPromotionService uses
            int eventsAttended = 0;
            if (rh is not null)
            {
                eventsAttended = await EventAttendanceHelper.CountEventsAttendedAsync(
                    db, guild.Id, member.Id,
                    rh.AssignedAt,
                    rh.SeedAppliedAt,
                    rh.EventsAttendedAtRankBeforeBot,
                    ct);
            }

            // Activity since rank-assigned: per-user query (small N — only members
            // who've already passed the time gate, capped at MaxPromotionCandidates)
            var msgsAtRank = await db.MessageEvents
                .CountAsync(m => m.GuildId == guild.Id
                              && m.UserId == member.Id
                              && m.Timestamp >= assignedAt, ct);

            var voiceSecsAtRank = await VoiceActivityHelper.GetVoiceSecondsAsync(
                db, guild.Id, member.Id, assignedAt,
                _config.MaxSingleSessionHours, ct);

            candidates.Add(new PromotionCandidate(
                Gamertag: member.DisplayName,
                CurrentRank: currentRank.ToUpperInvariant(),
                ProposedRank: tier.ToRank.ToUpperInvariant(),
                EventsAttendedAtRank: eventsAttended,
                MessagesAtRank: msgsAtRank,
                VoiceHoursAtRank: Math.Round(voiceSecsAtRank / 3600.0, 1),
                DaysInRank: daysInRank));
        }

        return candidates;
    }

    // ── Notable Events ──────────────────────────────────────────────────
    private async Task<List<NotableEvent>> GetNotableEventsAsync(
        SocketGuild guild,
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var events = await db.CalendarEvents
            .Where(c => c.GuildId == guild.Id
                     && c.EndUtc >= weekStart
                     && c.EndUtc <= weekEnd)
            .ToListAsync(ct);

        var notable = new List<NotableEvent>();
        foreach (var ev in events)
        {
            // "Attended" here = qualified via either pipeline (events VC ≥30min
            // OR meetings VC ≥15min). A given member could in theory be counted
            // in both for the same event; in practice the two tables target
            // disjoint VCs so double-counting is rare and never structural.
            var attendanceCount = await db.EventAttendances
                .CountAsync(a => a.GuildId == guild.Id && a.CalendarEventId == ev.Id, ct);
            attendanceCount += await db.MeetingAttendances
                .CountAsync(a => a.GuildId == guild.Id && a.CalendarEventId == ev.Id, ct);

            notable.Add(new NotableEvent(
                EventName: ev.Title,
                Date: ev.StartUtc,
                Attendance: attendanceCount,
                Source: ev.Source));
        }

        return notable;
    }

    // ── Risk Watch ──────────────────────────────────────────────────────
    // Members at [50%, 100%) of activity threshold on the closest axis.
    // Below 50% → about to be flagged AWOL anyway; ≥100% → safe.
    private Task<List<RiskWatchItem>> GetRiskWatchAsync(
        SocketGuild guild,
        IReadOnlyDictionary<int, BatchActivity> activityByWindow,
        CancellationToken ct)
    {
        var awolRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        var exemptRoles = _config.GetExemptRolesList();
        var rankRoles = _config.GetRankRolesList();

        var watch = new List<RiskWatchItem>();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (member.Roles.Any(r =>
                    exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))) continue;
            if (awolRole is not null && member.Roles.Any(r => r.Id == awolRole.Id)) continue;

            // Use the role-aware window so every member is measured against the
            // exact window AwolCheckService will judge them on.
            var windowDays = _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name));

            var ctx = ResolveWindowActivity(activityByWindow, windowDays);
            if (ctx is null) continue;   // no activity loaded for this window — nothing to say

            var msgs = ctx.MessageCounts.GetValueOrDefault(member.Id, 0);
            var voiceHours = ctx.VoiceSeconds.GetValueOrDefault(member.Id, 0d) / 3600.0;

            var msgRatio = _config.MinMessages > 0
                ? (double)msgs / _config.MinMessages
                : 1.0;
            var voiceRatio = _config.MinVoiceHours > 0
                ? voiceHours / _config.MinVoiceHours
                : 1.0;
            var closestRatio = Math.Max(msgRatio, voiceRatio);

            // Already meeting threshold on either axis = safe
            if (closestRatio >= RiskWatchUpperRatio) continue;
            // Way below = will be AWOL'd next cycle, not "trending"
            if (closestRatio < RiskWatchLowerRatio) continue;

            watch.Add(new RiskWatchItem(
                Gamertag: member.DisplayName,
                CurrentRank: GetCurrentRank(member, rankRoles) ?? "No Rank",
                Messages: msgs,
                VoiceHours: Math.Round(voiceHours, 1),
                RequiredMessages: _config.MinMessages,
                RequiredVoiceHours: _config.MinVoiceHours,
                WindowDays: windowDays,
                ClosestThresholdRatio: Math.Round(closestRatio, 2)));
        }

        return Task.FromResult(watch);
    }

    // Picks the loaded BatchActivity for a member's window. Exact hit is the
    // norm (the dictionary is built from the same two config values that
    // GetWindowDaysForRoles returns). The fallback only matters if config is
    // changed mid-run: prefer the largest loaded window that doesn't exceed
    // the member's, else the smallest available, so we never over-count.
    private static BatchActivity? ResolveWindowActivity(
        IReadOnlyDictionary<int, BatchActivity> activityByWindow, int windowDays)
    {
        if (activityByWindow.TryGetValue(windowDays, out var exact))
            return exact;
        if (activityByWindow.Count == 0)
            return null;

        var below = activityByWindow.Keys.Where(k => k <= windowDays).ToList();
        var key = below.Count > 0 ? below.Max() : activityByWindow.Keys.Min();
        return activityByWindow[key];
    }

    // ── Spotlight ───────────────────────────────────────────────────────
    // Top-scored member of the past 7 days using the weighted score above.
    // Returns null if nobody scored above zero (very quiet week).
    private async Task<MemberSpotlight?> GetSpotlightAsync(
        SocketGuild guild,
        BotDbContext db,
        BatchActivity ctx7d,
        CancellationToken ct)
    {
        var rankRoles = _config.GetRankRolesList();
        var exemptRoles = _config.GetExemptRolesList();

        // Spotlight highlights rank-and-file activity, so HQ leadership is
        // excluded from the pick. "HQ" = members carrying the configured HQ role
        // (the same role that gates officer tools/tickets). 0 = unset, in which
        // case no one is excluded on this basis.
        var hqRoleId = _config.OfficerAppHqRoleId;

        // Events attended in the 7-day window — single GROUP BY per table.
        // EventAttendance and MeetingAttendance are merged client-side because
        // the spotlight scores per-member and we need a unified count keyed by
        // UserId regardless of which pipeline credited them.
        var since = DateTime.UtcNow.AddDays(-SpotlightWindowDays);
        var eventCounts = await db.EventAttendances
            .Where(a => a.GuildId == guild.Id && a.EventEndUtc >= since)
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var meetingCounts = await db.MeetingAttendances
            .Where(a => a.GuildId == guild.Id && a.EventEndUtc >= since)
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        foreach (var (userId, count) in meetingCounts)
        {
            eventCounts[userId] = eventCounts.GetValueOrDefault(userId, 0) + count;
        }

        MemberSpotlight? best = null;
        int bestScore = 0;

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (member.Roles.Any(r =>
                    exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))) continue;
            if (hqRoleId != 0 && member.Roles.Any(r => r.Id == hqRoleId)) continue;

            var msgs = ctx7d.MessageCounts.GetValueOrDefault(member.Id, 0);
            var voiceHours = ctx7d.VoiceSeconds.GetValueOrDefault(member.Id, 0d) / 3600.0;
            var events = eventCounts.GetValueOrDefault(member.Id, 0);

            var score = msgs * ScorePerMessage
                      + (int)Math.Round(voiceHours * ScorePerVoiceHour)
                      + events * ScorePerEvent;

            if (score <= bestScore) continue;

            bestScore = score;
            best = new MemberSpotlight(
                Gamertag: member.DisplayName,
                CurrentRank: GetCurrentRank(member, rankRoles) ?? "No Rank",
                Messages: msgs,
                VoiceHours: Math.Round(voiceHours, 1),
                EventsAttended: events);
        }

        return bestScore > 0 ? best : null;
    }

    // ── Top-Line Numbers ────────────────────────────────────────────────
    private async Task<TopLineNumbers> GetTopLineNumbersAsync(
        SocketGuild guild,
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var exemptRoles = _config.GetExemptRolesList();

        var activeMembers = guild.Users.Count(u =>
            !u.IsBot &&
            !u.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase)));

        var attendanceSources = _config.AttendanceCountingSources
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var weeklyEvents = await db.CalendarEvents
            .Where(c => c.GuildId == guild.Id
                     && c.EndUtc >= weekStart
                     && c.EndUtc <= weekEnd)
            .ToListAsync(ct);

        var countingEvents = weeklyEvents
            .Where(e => attendanceSources.Contains(e.Source))
            .ToList();
        var eventsHeld = countingEvents.Count;

        double averageAttendancePercent = 0d;
        if (eventsHeld > 0 && activeMembers > 0)
        {
            var totalAttendances = 0;
            foreach (var ev in countingEvents)
            {
                // Same dual-pipeline merge as NotableEvents above — every
                // qualifying attendance from either VC counts toward the
                // "average attendance percent" headline number.
                totalAttendances += await db.EventAttendances
                    .CountAsync(a => a.GuildId == guild.Id && a.CalendarEventId == ev.Id, ct);
                totalAttendances += await db.MeetingAttendances
                    .CountAsync(a => a.GuildId == guild.Id && a.CalendarEventId == ev.Id, ct);
            }
            var avgPerEvent = (double)totalAttendances / eventsHeld;
            averageAttendancePercent = Math.Round((avgPerEvent / activeMembers) * 100, 1);
        }

        var windowAssignments = await db.RankHistories
            .Where(r => r.GuildId == guild.Id
                     && r.AssignedAt >= weekStart
                     && r.AssignedAt <= weekEnd)
            .ToListAsync(ct);
        var newRecruits = windowAssignments.Count(r =>
            r.RankName.Equals("RCT", StringComparison.OrdinalIgnoreCase));

        return new TopLineNumbers(activeMembers, eventsHeld, averageAttendancePercent, newRecruits);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string? GetCurrentRank(SocketGuildUser member, List<string> rankRoles)
    {
        var memberRoleNames = member.Roles.Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Contains(rankRoles[i]))
                return rankRoles[i];
        }
        return null;
    }

    private async Task<T> SafeAsync<T>(Func<Task<T>> work, string sectionName, T fallback)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Briefing section '{Section}' failed; using fallback", sectionName);
            return fallback;
        }
    }

    private static BriefingContext EmptyContext(DateTime weekStart, DateTime weekEnd) => new()
    {
        WeekStart = weekStart,
        WeekEnd = weekEnd,
        ActiveMemberCount = 0,
        EventsHeld = 0,
        AverageAttendancePercent = 0d,
        NewRecruits = 0
    };

    private sealed record TopLineNumbers(
        int ActiveMemberCount,
        int EventsHeld,
        double AverageAttendancePercent,
        int NewRecruits);

    private sealed record BatchActivity(
        IReadOnlyDictionary<ulong, int> MessageCounts,
        IReadOnlyDictionary<ulong, double> VoiceSeconds);
}