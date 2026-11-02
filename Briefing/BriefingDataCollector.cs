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
///   • RiskWatch — members who'll be flagged AWOL within a week if they go
///     quiet. Lets officers re-engage proactively before AwolCheckService flags them.
///   • Spotlight — single most-active member of the week by weighted score.
///   • WeekOverWeek — deltas vs the prior 7-day window for top-line numbers.
///
/// ── v3 enrichments ──
///   • RecruitmentSources + TopReferrers — joins this week broken down by
///     labeled invite source, plus top member referrers. Sourced from
///     InviteJoin via BriefingInviteSection. Independent of all other
///     sections; lives in its own helper class for clarity.
///
/// ── Sources of truth ──
/// Every number here should match what the bot itself acts on, so each one
/// reads the same table/role the owning feature does: AWOL = who holds the
/// AWOL role, recruits = the append-only RankChange log, joins = InviteJoin
/// (what /invite stats counts), promotion thresholds = AutoPromotionService's
/// own ladder, Risk Watch = AwolCheckService's window, thresholds and guards.
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
    /// <summary>The entry rank. Receiving it (see CountNewRecruitsAsync) is what makes someone a "new recruit".</summary>
    private const string RecruitRankName = "RCT";

    /// <summary>How far past an RCT row a matching rejoin timestamp may land. See CountNewRecruitsAsync.</summary>
    private static readonly TimeSpan RejoinMatchSlack = TimeSpan.FromMinutes(5);

    // Caps — these bound input token cost. Each bullet is roughly 30–70 input tokens.
    private const int MaxAwolRisks = 8;
    private const int MaxPromotionCandidates = 8;
    private const int MaxNotableEvents = 5;
    private const int MaxRiskWatch = 5;

    // Activity windows
    private const int ActivityContextWindowDays = 14;  // window for AWOL-item context

    // Spotlight scoring weights — tune to taste. Events are weighted highest
    // because they're scarce and high-effort; voice is mid (sustained presence);
    // messages are baseline (cheap signal).
    private const int ScorePerMessage    = 1;
    private const int ScorePerVoiceHour  = 5;
    private const int ScorePerEvent      = 10;

    /// <summary>
    /// How far ahead Risk Watch looks: a member surfaces if, with no new
    /// activity, AwolCheckService would flag them within this many days.
    /// One week lines up with the briefing cadence — everyone listed will be
    /// flagged before the next briefing unless an officer reaches them first.
    /// </summary>
    private const int RiskWatchLookaheadDays = 7;

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

        // Spotlight is the standout of the briefing week, so it reads exactly
        // [weekStart, weekEnd) — not "the 7 days before now", which drifts from
        // the week being reported on for a catch-up run that fires late or a
        // /briefing-now with explicit dates.
        var weekActivity = await LoadBatchActivityAsync(db, guild.Id, weekStart, weekEnd, ct);

        // Risk Watch must measure each member over their *own* AWOL window
        // (WindowDays for ranked members, ShortWindowDays for Guest, RCT and
        // anyone with no rank role) so its numbers mean the same thing
        // AwolCheckService's do. Using a single fixed
        // window here previously produced lines like "4/5 msgs over 28d" that
        // were actually 14d counts — a member with 300+ messages spread across
        // the month could surface as at-risk. GetWindowDaysForRoles only ever
        // returns one of these two values. Each window is loaded twice: as it
        // stands today, and the part of it that will still be inside the window
        // RiskWatchLookaheadDays from now (see GetRiskWatchAsync).
        var riskCurrentByWindow = new Dictionary<int, BatchActivity>();
        var riskRetainedByWindow = new Dictionary<int, BatchActivity>();
        foreach (var days in new[] { _config.WindowDays, _config.ShortWindowDays }
                     .Where(d => d > 0)
                     .Distinct())
        {
            riskCurrentByWindow[days] =
                await LoadBatchActivityAsync(db, guild.Id, now.AddDays(-days), now, ct);
            riskRetainedByWindow[days] = await LoadBatchActivityAsync(
                db, guild.Id, now.AddDays(-Math.Max(0, days - RiskWatchLookaheadDays)), now, ct);
        }

        var roster = GetRosterCounts(guild);

        // ── Sections, one at a time, each isolated from failures ──
        // Deliberately sequential. Every section shares the one BotDbContext
        // above, and EF Core does not allow overlapping operations on a
        // context: an overlap throws, SafeAsync swallows it, and that section
        // ships as its empty fallback — which the briefing then reports as a
        // real zero. Starting them all and awaiting Task.WhenAll only worked
        // as long as the SQLite provider happened to complete every query
        // synchronously. A weekly job loses nothing by not overlapping.
        var (awolItems, awolFlaggedThisWeek) = await SafeAsync(
            () => GetAwolRisksAsync(guild, db, ctx14d, weekStart, weekEnd, ct),
            "AWOL risks",
            (Items: new List<AwolRiskItem>(), FlaggedInWindow: 0));
        var promoAll  = await SafeAsync(() => GetPromotionCandidatesAsync(guild, db, ct), "promotion candidates", []);
        var eventsAll = await SafeAsync(() => GetNotableEventsAsync(guild, db, weekStart, weekEnd, ct), "notable events", []);
        var topLine = await SafeAsync(
            () => GetWindowNumbersAsync(guild, db, weekStart, weekEnd, roster.ActiveMembers, ct),
            "top-line numbers",
            new WindowNumbers(0, 0d, 0, 0));
        // Null on failure so the deltas below are dropped rather than computed
        // against zeros — "events up 4 WoW" against a prior week we failed to
        // read would be invented.
        var priorTopLine = await SafeAsync<WindowNumbers?>(
            async () => await GetWindowNumbersAsync(guild, db, weekStart.AddDays(-7), weekStart, roster.ActiveMembers, ct),
            "prior-week top-line",
            null);
        var intake    = await SafeAsync(() => GetIntakeAsync(guild, db, weekStart, weekEnd, ct), "intake", null);
        var spotlight = await SafeAsync(() => GetSpotlightAsync(guild, db, weekActivity, weekStart, weekEnd, ct), "spotlight", null);
        var riskAll   = await SafeAsync(
            () => GetRiskWatchAsync(guild, db, riskCurrentByWindow, riskRetainedByWindow, now, ct),
            "risk watch",
            []);

        // ── v3: Recruitment sources + top referrers ──
        // Lives in BriefingInviteSection so the new section doesn't tangle
        // with the existing collector logic. Empty fallback covers both
        // sub-results in one shot.
        var (recruitmentSources, topReferrers) = await SafeAsync(
            () => BriefingInviteSection.CollectAsync(guild, db, weekStart, weekEnd, ct),
            "recruitment sources",
            (Sources: (IReadOnlyList<RecruitmentSourceItem>)Array.Empty<RecruitmentSourceItem>(),
             Referrers: (IReadOnlyList<TopReferrerItem>)Array.Empty<TopReferrerItem>()));

        // ── Recruit leads (Reddit RSS-sourced funnel) ──
        // Same external-helper pattern as the invite section. Returns null when
        // the briefing window saw zero leads — the prompt treats null as "skip
        // the section" so disabled-feature and quiet-week look identical.
        var redditLeads = await SafeAsync<RedditLeadsSnapshot?>(
            () => BriefingRedditLeadsSection.CollectAsync(db, weekStart, weekEnd, ct),
            "recruit leads",
            null);

        // ── Retention (member departures) ──
        // Same external-helper + null-means-skip pattern. Gated on the feature
        // flag so the section can be hidden while the prompt is tuned without
        // affecting capture.
        var retention = _config.RetentionSectionEnabled
            ? await SafeAsync<RetentionSnapshot?>(
                () => BriefingRetentionSection.CollectAsync(
                    db, guild.Id, weekStart, weekEnd,
                    joinsThisWeek: topLine.TotalJoins, _config.GetRankRolesList(), ct),
                "retention",
                null)
            : null;

        // ── Poll participation ──
        // Same external-helper + null-means-skip pattern. Null when no polls were
        // created this week and none are open.
        var polls = await SafeAsync<PollsSnapshot?>(
            () => BriefingPollSection.CollectAsync(
                db, guild, weekStart, weekEnd, _config.GetExemptRolesList(), _config.AwolRoleName, ct),
            "polls",
            null);

        // Longest-flagged first; a hand-applied role with no record sorts last.
        var awolRisks = awolItems
            .OrderByDescending(r => r.DaysSinceAwolAssigned ?? -1)
            .Take(MaxAwolRisks)
            .ToList();

        // Members who already clear the activity bar first — they're the ones
        // an officer can act on (or that a dry-run tier is holding back).
        var promotionCandidates = promoAll
            .OrderByDescending(c => c.MeetsActivity)
            .ThenByDescending(c => c.DaysInRank)
            .Take(MaxPromotionCandidates)
            .ToList();

        var notableEvents = eventsAll
            .OrderByDescending(e => e.Attendance)
            .Take(MaxNotableEvents)
            .ToList();

        // Ranked members before Guests (losing a member outweighs losing a
        // tire-kicker), then the most engaged first — they're the easiest saves.
        var riskWatch = riskAll
            .OrderByDescending(r => r.CurrentRank != NoRankLabel)
            .ThenByDescending(r => r.ClosestThresholdRatio)
            .Take(MaxRiskWatch)
            .ToList();

        var deltas = priorTopLine is null
            ? null
            : new WeekOverWeekDeltas(
                EventsHeldDelta: topLine.EventsHeld - priorTopLine.EventsHeld,
                AverageAttendancePercentDelta: Math.Round(
                    topLine.AverageAttendancePercent - priorTopLine.AverageAttendancePercent, 1),
                NewRecruitsDelta: topLine.NewRecruits - priorTopLine.NewRecruits,
                TotalJoinsDelta: topLine.TotalJoins - priorTopLine.TotalJoins);

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
            TotalMembers = roster.TotalMembers,
            ActiveMemberCount = roster.ActiveMembers,
            GuestCount = roster.Guests,
            AwolCount = roster.Awol,
            EventsHeld = topLine.EventsHeld,
            AverageAttendancePercent = topLine.AverageAttendancePercent,
            NewRecruits = topLine.NewRecruits,
            TotalJoins = topLine.TotalJoins,
            Intake = intake,
            WeekOverWeek = deltas,
            AwolRisks = awolRisks,
            AwolFlaggedThisWeek = awolFlaggedThisWeek,
            PromotionCandidates = promotionCandidates,
            PromotionCandidatesTotal = promoAll.Count,
            NotableEvents = notableEvents,
            RiskWatch = riskWatch,
            RiskWatchTotal = riskAll.Count,
            Spotlight = spotlight,
            RecruitmentSources = recruitmentSources,
            TopReferrers = topReferrers,
            RedditLeads = redditLeads,
            Retention = retention,
            Polls = polls,
            Anomalies = anomalies
        };

        _logger.LogInformation(
            "Briefing context: {Awol} AWOL ({AwolNew} flagged this week), {Promo} promo candidates, " +
            "{Events} events, {Risk} risk-watch, {Joins} joins → {Recruits} new RCTs, " +
            "{Sources} sources, {Referrers} referrers, " +
            "{Leads} recruit leads (stale claimed: {Stale}), " +
            "{Departures} departures (net {Net}), " +
            "spotlight={Spot}, {Members} active of {Total} members ({Guests} guests), " +
            "WoW: events Δ{EventsDelta}, attendance Δ{AttendanceDelta}pp, recruits Δ{RecruitsDelta}, " +
            "joins Δ{JoinsDelta}, {Anomalies} anomalies",
            roster.Awol, awolFlaggedThisWeek, promoAll.Count,
            notableEvents.Count, riskAll.Count, topLine.TotalJoins, topLine.NewRecruits,
            recruitmentSources.Count, topReferrers.Count,
            redditLeads?.TotalSurfaced ?? 0, redditLeads?.StaleClaimedAllTime ?? 0,
            retention?.TotalDepartures ?? 0, retention?.NetMembershipChange ?? 0,
            spotlight?.Gamertag ?? "—", roster.ActiveMembers, roster.TotalMembers, roster.Guests,
            deltas?.EventsHeldDelta, deltas?.AverageAttendancePercentDelta, deltas?.NewRecruitsDelta,
            deltas?.TotalJoinsDelta, anomalies.Count);

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
    // Built from who holds the AWOL role right now. This used to list pending
    // AwolRecords (NotificationSent == false), but AwolCheckService marks a
    // record sent as soon as it posts the HQ notice, AwolGraceDays after the
    // flag. So only members flagged in the last couple of days ever showed up,
    // and everyone who had been AWOL longer — the ones officers act on — was
    // missing. The records are still used, for when each AWOL spell started.
    private async Task<(List<AwolRiskItem> Items, int FlaggedInWindow)> GetAwolRisksAsync(
        SocketGuild guild,
        BotDbContext db,
        BatchActivity ctx14d,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        // Aggregated in memory — a few hundred rows at most, and it avoids
        // leaning on EF Core's SQLite translation of MAX over a DateTime.
        var records = await db.AwolRecords
            .Where(r => r.GuildId == guild.Id)
            .Select(r => new { r.UserId, r.AssignedAt })
            .ToListAsync(ct);

        var flaggedInWindow = records
            .Where(r => r.AssignedAt >= weekStart && r.AssignedAt < weekEnd)
            .Select(r => r.UserId)
            .Distinct()
            .Count();

        var awolRole = FindAwolRole(guild);
        if (awolRole is null) return ([], flaggedInWindow);

        // Latest flag per member = the start of their current AWOL spell.
        var lastFlagged = records
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.Max(r => r.AssignedAt));

        var rankRoles = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        var risks = new List<AwolRiskItem>();
        foreach (var member in guild.Users)
        {
            if (member.IsBot || !IsAwol(member, awolRole)) continue;

            int? daysSince = lastFlagged.TryGetValue(member.Id, out var assignedAt)
                ? (int)(now - assignedAt).TotalDays
                : null;

            var msgs14d = ctx14d.MessageCounts.GetValueOrDefault(member.Id, 0);
            var voice14d = ctx14d.VoiceSeconds.GetValueOrDefault(member.Id, 0d) / 3600.0;

            var trendNote = daysSince switch
            {
                null => "holds the AWOL role with no AWOL record on file — likely applied by hand",
                var d when d >= _config.AwolGraceDays =>
                    $"flagged {d}d ago, grace period elapsed — ready for officer review",
                var d => $"flagged {d}d ago, in {_config.AwolGraceDays}-day grace period",
            };

            risks.Add(new AwolRiskItem(
                Gamertag: member.DisplayName,
                CurrentRank: GetCurrentRank(member, rankRoles) ?? NoRankLabel,
                DaysSinceAwolAssigned: daysSince,
                Messages14d: msgs14d,
                VoiceHours14d: Math.Round(voice14d, 1),
                TrendNote: trendNote));
        }

        return (risks, flaggedInWindow);
    }

    // ── Promotion Candidates ────────────────────────────────────────────
    // Members who've served their tier's time-in-rank, judged the way the
    // nightly AutoPromotionService judges them: its own tier table (via
    // GetTierForRank, not a copy that can drift), its effective rank date
    // (PromotionService.GetRankInfoAsync's rejoin rule, mirrored below), and
    // its activity bar. Since that job promotes anyone who clears both gates,
    // most entries here are members held back by activity — MeetsActivity
    // says which, so the briefing no longer has to guess.
    private async Task<List<PromotionCandidate>> GetPromotionCandidatesAsync(
        SocketGuild guild,
        BotDbContext db,
        CancellationToken ct)
    {
        var awolRole = FindAwolRole(guild);
        var rankRoles = _config.GetRankRolesList();
        var dryRunRanks = _config.GetAutoPromotionDryRunRanksList();
        var now = DateTime.UtcNow;

        var rankHistories = await db.RankHistories
            .Where(r => r.GuildId == guild.Id)
            .ToDictionaryAsync(r => r.UserId, ct);

        var candidates = new List<PromotionCandidate>();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (IsAwol(member, awolRole)) continue;

            var currentRank = GetCurrentRank(member, rankRoles);
            if (currentRank is null) continue;
            var tier = AutoPromotionService.GetTierForRank(currentRank);
            if (tier is null) continue;

            // Effective rank start + seed, same rules as GetRankInfoAsync: a
            // RankHistory row older than the member's latest join is left over
            // from a previous membership, so the clock restarts at the join and
            // the seed credit doesn't carry over.
            var joinedAt = member.JoinedAt?.UtcDateTime;
            DateTime? assignedAt = joinedAt;
            DateTime? seedAppliedAt = null;
            var seedEvents = 0;
            if (rankHistories.TryGetValue(member.Id, out var rh)
                && !(joinedAt.HasValue && joinedAt.Value > rh.AssignedAt))
            {
                assignedAt = rh.AssignedAt;
                seedAppliedAt = rh.SeedAppliedAt;
                seedEvents = rh.EventsAttendedAtRankBeforeBot;
            }
            if (assignedAt is null) continue;

            var daysInRank = (int)(now - assignedAt.Value).TotalDays;
            if (daysInRank < tier.DaysInRank) continue;

            // Cumulative events at rank — same helper AutoPromotionService uses
            var eventsAttended = await EventAttendanceHelper.CountEventsAttendedAsync(
                db, guild.Id, member.Id,
                assignedAt.Value,
                seedAppliedAt,
                seedEvents,
                ct);

            // Activity since rank-assigned: per-user query (small N — only members
            // who've already passed the time gate)
            var msgsAtRank = await db.MessageEvents
                .CountAsync(m => m.GuildId == guild.Id
                              && m.UserId == member.Id
                              && m.Timestamp >= assignedAt.Value, ct);

            var voiceHoursAtRank = await VoiceActivityHelper.GetVoiceSecondsAsync(
                db, guild.Id, member.Id, assignedAt.Value,
                _config.MaxSingleSessionHours, ct) / 3600.0;

            var meetsActivity = tier.MinEvents > 0
                ? eventsAttended >= tier.MinEvents
                : msgsAtRank >= tier.MinMessages || voiceHoursAtRank >= tier.MinVoiceHours;

            var autoPromoteEnabled = !_config.AutoPromotionDryRun
                && !dryRunRanks.Any(r => r.Equals(tier.FromRank, StringComparison.OrdinalIgnoreCase));

            candidates.Add(new PromotionCandidate(
                Gamertag: member.DisplayName,
                CurrentRank: currentRank.ToUpperInvariant(),
                ProposedRank: tier.ToRank.ToUpperInvariant(),
                EventsAttendedAtRank: eventsAttended,
                MessagesAtRank: msgsAtRank,
                VoiceHoursAtRank: Math.Round(voiceHoursAtRank, 1),
                DaysInRank: daysInRank,
                RequiredEvents: tier.MinEvents,
                RequiredMessages: tier.MinMessages,
                RequiredVoiceHours: tier.MinVoiceHours,
                MeetsActivity: meetsActivity,
                AutoPromoteEnabled: autoPromoteEnabled));
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
                     && c.EndUtc < weekEnd)
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
    // Who will AwolCheckService flag within RiskWatchLookaheadDays if they go
    // quiet? That sweep flags anyone below threshold on BOTH axes as soon as
    // its join/reset guards allow, so the old definition — "50–99% of
    // threshold but not flagged yet" — only ever caught members a guard was
    // still protecting, mostly brand-new joiners, and never the established
    // members whose activity was about to age out. Instead, project a week
    // ahead assuming no new activity: re-check the same guards against the
    // window as it will stand then, and test the activity that will still be
    // inside it, which is whatever's in the last (window − lookahead) days.
    private async Task<List<RiskWatchItem>> GetRiskWatchAsync(
        SocketGuild guild,
        BotDbContext db,
        IReadOnlyDictionary<int, BatchActivity> currentByWindow,
        IReadOnlyDictionary<int, BatchActivity> retainedByWindow,
        DateTime now,
        CancellationToken ct)
    {
        var awolRole = FindAwolRole(guild);
        var exemptRoles = _config.GetExemptRolesList();
        var rankRoles = _config.GetRankRolesList();
        var minVoiceSeconds = _config.MinVoiceHours * 3600;

        // Same per-member reset stamps AwolCheckService's window-reset guard reads.
        var windowResetByUser = (await db.UserActivities
                .Where(a => a.GuildId == guild.Id && a.WindowResetAt != null)
                .Select(a => new { a.UserId, a.WindowResetAt })
                .ToListAsync(ct))
            .ToDictionary(a => a.UserId, a => a.WindowResetAt!.Value);

        var watch = new List<RiskWatchItem>();

        foreach (var member in guild.Users)
        {
            if (member.IsBot) continue;
            if (IsExempt(member, exemptRoles)) continue;
            if (IsAwol(member, awolRole)) continue;

            // Use the role-aware window so every member is measured against the
            // exact window AwolCheckService will judge them on.
            var windowDays = _config.GetWindowDaysForRoles(member.Roles.Select(r => r.Name));

            // Start of the window the sweep will use a lookahead from now. If
            // either of its guards still applies then (joined too recently, or
            // window reset too recently), they can't be flagged this week.
            var futureWindowStart = now.AddDays(RiskWatchLookaheadDays - windowDays);
            if (member.JoinedAt.HasValue && member.JoinedAt.Value.UtcDateTime > futureWindowStart) continue;
            if (windowResetByUser.TryGetValue(member.Id, out var resetAt) && resetAt > futureWindowStart) continue;

            var current = ResolveWindowActivity(currentByWindow, windowDays);
            var retained = ResolveWindowActivity(retainedByWindow, windowDays);
            if (current is null || retained is null) continue;   // nothing loaded for this window

            // Already banked enough to clear next week's sweep without doing anything.
            var retainedMsgs = retained.MessageCounts.GetValueOrDefault(member.Id, 0);
            var retainedVoiceSeconds = retained.VoiceSeconds.GetValueOrDefault(member.Id, 0d);
            if (retainedMsgs >= _config.MinMessages || retainedVoiceSeconds >= minVoiceSeconds) continue;

            var msgs = current.MessageCounts.GetValueOrDefault(member.Id, 0);
            var voiceHours = current.VoiceSeconds.GetValueOrDefault(member.Id, 0d) / 3600.0;

            var msgRatio = _config.MinMessages > 0
                ? (double)msgs / _config.MinMessages
                : 1.0;
            var voiceRatio = _config.MinVoiceHours > 0
                ? voiceHours / _config.MinVoiceHours
                : 1.0;

            watch.Add(new RiskWatchItem(
                Gamertag: member.DisplayName,
                CurrentRank: GetCurrentRank(member, rankRoles) ?? NoRankLabel,
                Messages: msgs,
                VoiceHours: Math.Round(voiceHours, 1),
                RetainedMessages: retainedMsgs,
                RetainedVoiceHours: Math.Round(retainedVoiceSeconds / 3600.0, 1),
                RequiredMessages: _config.MinMessages,
                RequiredVoiceHours: _config.MinVoiceHours,
                WindowDays: windowDays,
                ClosestThresholdRatio: Math.Round(Math.Max(msgRatio, voiceRatio), 2)));
        }

        return watch;
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
    // Top-scored member of the briefing week using the weighted score above.
    // Returns null if nobody scored above zero (very quiet week).
    private async Task<MemberSpotlight?> GetSpotlightAsync(
        SocketGuild guild,
        BotDbContext db,
        BatchActivity weekActivity,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var rankRoles = _config.GetRankRolesList();
        var exemptRoles = _config.GetExemptRolesList();

        // Spotlight highlights rank-and-file activity, so HQ leadership is
        // excluded from the pick. "HQ" = members carrying the configured HQ role
        // (the same role that gates officer tools/tickets). 0 = unset, in which
        // case no one is excluded on this basis.
        var hqRoleId = _config.OfficerAppHqRoleId;

        // Events attended in the briefing week — single GROUP BY per table.
        // EventAttendance and MeetingAttendance are merged client-side because
        // the spotlight scores per-member and we need a unified count keyed by
        // UserId regardless of which pipeline credited them.
        var eventCounts = await db.EventAttendances
            .Where(a => a.GuildId == guild.Id && a.EventEndUtc >= weekStart && a.EventEndUtc < weekEnd)
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

        var meetingCounts = await db.MeetingAttendances
            .Where(a => a.GuildId == guild.Id && a.EventEndUtc >= weekStart && a.EventEndUtc < weekEnd)
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

            var msgs = weekActivity.MessageCounts.GetValueOrDefault(member.Id, 0);
            var voiceHours = weekActivity.VoiceSeconds.GetValueOrDefault(member.Id, 0d) / 3600.0;
            var events = eventCounts.GetValueOrDefault(member.Id, 0);

            var score = msgs * ScorePerMessage
                      + (int)Math.Round(voiceHours * ScorePerVoiceHour)
                      + events * ScorePerEvent;

            if (score <= bestScore) continue;

            bestScore = score;
            best = new MemberSpotlight(
                Gamertag: member.DisplayName,
                CurrentRank: GetCurrentRank(member, rankRoles) ?? NoRankLabel,
                Messages: msgs,
                VoiceHours: Math.Round(voiceHours, 1),
                EventsAttended: events);
        }

        return bestScore > 0 ? best : null;
    }

    // ── Roster Counts ───────────────────────────────────────────────────
    // Live headcount, not windowed. "Active" used to be every non-bot,
    // non-exempt account in the server — Guests who never onboarded and AWOL
    // members included — which overstated the roster and, as the attendance
    // denominator, understated attendance. It's now the ranked, non-AWOL
    // roster, with Guests and AWOL reported alongside.
    private RosterCounts GetRosterCounts(SocketGuild guild)
    {
        var exemptRoles = _config.GetExemptRolesList();
        var rankRoles = _config.GetRankRolesList();
        var awolRole = FindAwolRole(guild);

        var humans = guild.Users.Where(u => !u.IsBot).ToList();
        var nonExempt = humans.Where(u => !IsExempt(u, exemptRoles)).ToList();

        return new RosterCounts(
            TotalMembers: humans.Count,
            ActiveMembers: nonExempt.Count(u => GetCurrentRank(u, rankRoles) is not null && !IsAwol(u, awolRole)),
            Guests: nonExempt.Count(u => GetCurrentRank(u, rankRoles) is null),
            Awol: humans.Count(u => IsAwol(u, awolRole)));
    }

    // ── Window Numbers ──────────────────────────────────────────────────
    // Computed for the briefing week and again for the week before it (for
    // the WoW deltas), so everything here must be answerable for a PAST
    // window — i.e. read from append-only logs, never from current state.
    private async Task<WindowNumbers> GetWindowNumbersAsync(
        SocketGuild guild,
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        int activeMembers,
        CancellationToken ct)
    {
        var attendanceSources = _config.AttendanceCountingSources
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Half-open like every other window here. This was inclusive at both
        // ends, so an event ending exactly on the boundary counted in both the
        // briefing week and the prior week.
        var weeklyEvents = await db.CalendarEvents
            .Where(c => c.GuildId == guild.Id
                     && c.EndUtc >= weekStart
                     && c.EndUtc < weekEnd)
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

        // Recruits come from the append-only RankChange log. This used to read
        // RankHistory, which holds only each CURRENT member's CURRENT rank: a
        // recruit who'd since been promoted RCT → PVT (7 days, automatic) or
        // had left the server simply vanished from the count. That made this
        // week's number an undercount and the prior week's — a full round of
        // RCT → PVT promotions later — close to zero every time.
        var newRecruits = await CountNewRecruitsAsync(
            db, guild.Id, id => guild.GetUser(id)?.JoinedAt?.UtcDateTime, weekStart, weekEnd, ct);

        // Every server join, the same rows /invite stats counts.
        var totalJoins = await db.InviteJoins
            .CountAsync(j => j.GuildId == guild.Id
                          && j.JoinedAt >= weekStart
                          && j.JoinedAt < weekEnd, ct);

        return new WindowNumbers(eventsHeld, averageAttendancePercent, newRecruits, totalJoins);
    }

    // ── New Recruits ────────────────────────────────────────────────────
    // Distinct members given RCT in the window for the first time in their
    // current stay in the server. Being handed RCT isn't enough on its own:
    // when the role comes off and goes back on, the log records a removal and
    // then a fresh "Initial" RCT row — identical to a brand-new arrival — and
    // a demotion back to RCT is an RCT row too. So an RCT row only counts if
    // the member has no earlier rank in the log, or left and came back since
    // that earlier row (a rejoiner is re-recruited, and shows up in joins too).
    //
    // "Left and came back" accepts any of three signals between the earlier
    // row and the RCT row: Discord's join date for a current member (the same
    // test RankRestoreHelper uses to refuse carrying a rank across a leave), a
    // MemberDeparture, or an InviteJoin. The last two cover members who have
    // since left again, and rejoins from before departure tracking existed.
    //
    // currentJoinedAtUtc is the member's live guild join date, or null if
    // they're no longer in the server. Passed in rather than read off the
    // guild so this can be exercised without a gateway connection.
    private static async Task<int> CountNewRecruitsAsync(
        BotDbContext db,
        ulong guildId,
        Func<ulong, DateTime?> currentJoinedAtUtc,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var rctRows = await db.RankChanges
            .Where(c => c.GuildId == guildId
                     && c.ToRank != null
                     && c.ToRank.ToUpper() == RecruitRankName
                     && c.ChangedAt >= weekStart
                     && c.ChangedAt < weekEnd)
            .Select(c => new { c.UserId, c.ChangedAt })
            .ToListAsync(ct);

        if (rctRows.Count == 0) return 0;

        var userIds = rctRows.Select(r => r.UserId).Distinct().ToList();

        // Only these members' rows — dozens at most — matched up in memory.
        var rankHistory = (await db.RankChanges
                .Where(c => c.GuildId == guildId && userIds.Contains(c.UserId) && c.ChangedAt < weekEnd)
                .Select(c => new { c.UserId, c.ChangedAt })
                .ToListAsync(ct))
            .ToLookup(c => c.UserId, c => c.ChangedAt);

        var departures = (await db.MemberDepartures
                .Where(d => d.GuildId == guildId && userIds.Contains(d.UserId))
                .Select(d => new { d.UserId, d.DepartedAt })
                .ToListAsync(ct))
            .ToLookup(d => d.UserId, d => d.DepartedAt);

        var joins = (await db.InviteJoins
                .Where(j => j.GuildId == guildId && userIds.Contains(j.UserDiscordId))
                .Select(j => new { j.UserDiscordId, j.JoinedAt })
                .ToListAsync(ct))
            .ToLookup(j => j.UserDiscordId, j => j.JoinedAt);

        bool IsFirstRankThisStay(ulong userId, DateTime rctAt)
        {
            var earlier = rankHistory[userId].Where(t => t < rctAt).ToList();
            if (earlier.Count == 0) return true;

            // A rejoin lands just before the RCT row it produces, but the RCT row
            // can be dated to Discord's join time while InviteJoin is stamped when
            // the bot handled it, a moment later — hence the slack on the end.
            var lastRanked = earlier.Max();
            var rejoinBy = rctAt + RejoinMatchSlack;
            bool Between(DateTime t) => t > lastRanked && t <= rejoinBy;

            return currentJoinedAtUtc(userId) is { } joinedAt && Between(joinedAt)
                || departures[userId].Any(Between)
                || joins[userId].Any(Between);
        }

        return rctRows
            .Where(r => IsFirstRankThisStay(r.UserId, r.ChangedAt))
            .Select(r => r.UserId)
            .Distinct()
            .Count();
    }

    // ── Intake ──────────────────────────────────────────────────────────
    // Where the week's joiners stand now: still here, and onboarded to a
    // rank. Current state, so this is briefing-week only (no WoW delta).
    private async Task<IntakeSnapshot?> GetIntakeAsync(
        SocketGuild guild,
        BotDbContext db,
        DateTime weekStart,
        DateTime weekEnd,
        CancellationToken ct)
    {
        var joinerIds = await db.InviteJoins
            .Where(j => j.GuildId == guild.Id
                     && j.JoinedAt >= weekStart
                     && j.JoinedAt < weekEnd)
            .Select(j => j.UserDiscordId)
            .Distinct()
            .ToListAsync(ct);

        if (joinerIds.Count == 0) return null;

        var rankRoles = _config.GetRankRolesList();
        var stillHere = joinerIds
            .Select(id => guild.GetUser(id))
            .Where(m => m is not null)
            .ToList();

        return new IntakeSnapshot(
            UniqueJoiners: joinerIds.Count,
            StillInServer: stillHere.Count,
            NowRanked: stillHere.Count(m => GetCurrentRank(m!, rankRoles) is not null));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>CurrentRank shown for a member holding no rank role.</summary>
    private const string NoRankLabel = "No Rank";

    private SocketRole? FindAwolRole(SocketGuild guild) =>
        guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));

    private static bool IsAwol(SocketGuildUser member, SocketRole? awolRole) =>
        awolRole is not null && member.Roles.Any(r => r.Id == awolRole.Id);

    private static bool IsExempt(SocketGuildUser member, List<string> exemptRoles) =>
        member.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));

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

    private sealed record RosterCounts(
        int TotalMembers,
        int ActiveMembers,
        int Guests,
        int Awol);

    private sealed record WindowNumbers(
        int EventsHeld,
        double AverageAttendancePercent,
        int NewRecruits,
        int TotalJoins);

    private sealed record BatchActivity(
        IReadOnlyDictionary<ulong, int> MessageCounts,
        IReadOnlyDictionary<ulong, double> VoiceSeconds);
}