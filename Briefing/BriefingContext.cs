namespace ClanGuardBot.Briefing;

/// <summary>
/// Pre-aggregated, pre-filtered weekly snapshot sent to Claude.
///
/// COST NOTE: Every field here was filtered or aggregated in C# before
/// reaching the prompt. We send a few dozen line items, NOT the full roster
/// + attendance log. With the v2 enrichments (per-item activity context,
/// spotlight, risk watch, week-over-week deltas) target serialized size is
/// ~1.5K input tokens, up from ~600 in v1. Per-run cost lands around $0.02.
///
/// ── v3 enrichment: Recruitment Sources ──
/// RecruitmentSources + TopReferrers add ~50 input tokens for an active
/// week (negligible). Sourced from the InviteJoin table written by
/// InviteAttributionService. Both are empty when the week saw zero
/// attributable joins.
/// </summary>
public sealed record BriefingContext
{
    public required DateTime WeekStart { get; init; }
    public required DateTime WeekEnd { get; init; }

    // Top-line numbers — cheap to include, anchor the briefing
    public required int ActiveMemberCount { get; init; }
    public required int EventsHeld { get; init; }
    public required double AverageAttendancePercent { get; init; }
    public required int NewRecruits { get; init; }

    /// <summary>Week-over-week deltas vs the prior 7-day window. Null if prior-week data couldn't be computed.</summary>
    public WeekOverWeekDeltas? WeekOverWeek { get; init; }

    // Attention items — pre-filtered to top N each, capped in the collector
    public IReadOnlyList<AwolRiskItem> AwolRisks { get; init; } = [];
    public IReadOnlyList<PromotionCandidate> PromotionCandidates { get; init; } = [];
    public IReadOnlyList<NotableEvent> NotableEvents { get; init; } = [];

    /// <summary>
    /// Members trending toward AWOL (50–99% of activity threshold, not yet
    /// flagged). Officers can re-engage them BEFORE the AwolCheckService
    /// flags them. Empty if nobody is in the danger zone.
    /// </summary>
    public IReadOnlyList<RiskWatchItem> RiskWatch { get; init; } = [];

    /// <summary>
    /// Most-active member of the week by combined score (messages + voice + events).
    /// Null if there isn't a clear standout (e.g. very quiet week).
    /// </summary>
    public MemberSpotlight? Spotlight { get; init; }

    /// <summary>
    /// Joins in the briefing week broken down by labeled invite source.
    /// Sorted descending by JoinCount in the collector. Empty when no joins
    /// happened (or none were attributable). Labels are the verbatim values
    /// from InviteJoin.LabelSnapshot — both real labels ("Website") and
    /// sentinels ("Vanity", "Unknown", "Ambiguous", "Unattributed") all
    /// surface here. Claude can choose to call out sentinels or skip them
    /// based on the prompt guidance.
    /// </summary>
    public IReadOnlyList<RecruitmentSourceItem> RecruitmentSources { get; init; } = [];

    /// <summary>
    /// Top members by personal-invite referrals in the week. Sourced from
    /// InviteJoin.InviterDiscordId, which Discord populates only for joins
    /// via regular invites (not vanity, not server discovery). Capped at a
    /// small N in the collector. Empty when nobody on the roster has any
    /// referred joins.
    /// </summary>
    public IReadOnlyList<TopReferrerItem> TopReferrers { get; init; } = [];

    /// <summary>
    /// Snapshot of the Reddit recruit-lead funnel for the briefing week.
    /// Null when no leads were surfaced in the window — the prompt skips
    /// the section in that case, matching the Spotlight convention.
    /// Sourced from the RedditLeads table written by RedditLeadService.
    /// </summary>
    public RedditLeadsSnapshot? RedditLeads { get; init; }

    /// <summary>
    /// Retention snapshot for the briefing week. Null when zero departures
    /// occurred — the prompt treats null as "skip the section," matching the
    /// Spotlight / RedditLeads convention. Sourced from the MemberDeparture
    /// table via BriefingRetentionSection.
    /// </summary>
    public RetentionSnapshot? Retention { get; init; }

    /// <summary>Free-form short notes for things that don't fit the other buckets.</summary>
    public IReadOnlyList<string> Anomalies { get; init; } = [];
}

/// <param name="DaysSinceAwolAssigned">Days since the AWOL role was assigned.</param>
/// <param name="Messages14d">Messages sent in the last 14 days. 0 = fully checked out.</param>
/// <param name="VoiceHours14d">Voice hours in the last 14 days. Capped per session per BotConfig.MaxSingleSessionHours.</param>
/// <param name="TrendNote">One-line context, e.g. "flagged 5d ago, grace period elapsed".</param>
public sealed record AwolRiskItem(
    string Gamertag,
    string CurrentRank,
    int DaysSinceAwolAssigned,
    int Messages14d,
    double VoiceHours14d,
    string TrendNote);

/// <param name="EventsAttendedAtRank">Cumulative events at current rank (bot-tracked + seed). Used by promotion math directly.</param>
/// <param name="MessagesAtRank">Messages sent since rank was assigned.</param>
/// <param name="VoiceHoursAtRank">Voice hours since rank was assigned (capped per session).</param>
public sealed record PromotionCandidate(
    string Gamertag,
    string CurrentRank,
    string ProposedRank,
    int EventsAttendedAtRank,
    int MessagesAtRank,
    double VoiceHoursAtRank,
    int DaysInRank);

/// <param name="Source">"Clan" or "CompDiv" — matches the existing event source filter.</param>
public sealed record NotableEvent(
    string EventName,
    DateTime Date,
    int Attendance,
    string Source);

/// <summary>
/// Member trending toward AWOL: not yet flagged, but below the activity
/// threshold on at least one axis with the closest ratio between 50% and 100%.
/// "Below 50%" is excluded as those members are about to be flagged AWOL by
/// AwolCheckService anyway and would clutter this section.
/// </summary>
/// <param name="ClosestThresholdRatio">Closest threshold ratio reached (0.5–1.0). Higher = closer to safe.</param>
public sealed record RiskWatchItem(
    string Gamertag,
    string CurrentRank,
    int Messages,
    double VoiceHours,
    int RequiredMessages,
    double RequiredVoiceHours,
    int WindowDays,
    double ClosestThresholdRatio);

/// <param name="Score">Combined score (1pt/msg + 5pts/voice hour + 10pts/event). Tunable in BriefingDataCollector.</param>
public sealed record MemberSpotlight(
    string Gamertag,
    string CurrentRank,
    int Messages,
    double VoiceHours,
    int EventsAttended,
    int Score);

/// <summary>
/// Week-over-week deltas. Negative numbers mean "worse than last week"
/// (fewer events, lower attendance, fewer recruits). Null fields mean the
/// prior week's data couldn't be computed (e.g. brand-new database).
/// </summary>
/// <param name="AverageAttendancePercentDelta">Percentage-point change, e.g. -12.5 means attendance dropped 12.5pp vs last week.</param>
public sealed record WeekOverWeekDeltas(
    int EventsHeldDelta,
    double AverageAttendancePercentDelta,
    int NewRecruitsDelta);

/// <summary>
/// Single label's recruitment count for the briefing week.
/// </summary>
/// <param name="Label">
/// The InviteJoin.LabelSnapshot value. Real labels ("Website", "Facebook")
/// surface alongside sentinel values ("Vanity", "Unknown", "Ambiguous",
/// "Unattributed") so Claude has the full picture.
/// </param>
/// <param name="JoinCount">How many joins were tagged with this label in the briefing week.</param>
public sealed record RecruitmentSourceItem(
    string Label,
    int JoinCount);

/// <summary>
/// Single referrer's count for the briefing week. Discord populates the
/// inviter only for joins via regular invites — vanity / discovery / unknown
/// joins won't credit anyone here.
/// </summary>
/// <param name="Username">Best-effort display name. Snapshotted on the InviteJoin row.</param>
/// <param name="ReferralCount">Joins in the briefing week with InviterDiscordId == this user.</param>
public sealed record TopReferrerItem(
    string Username,
    ulong DiscordUserId,
    int ReferralCount);

/// <summary>
/// Briefing-week snapshot of the Reddit recruit-lead funnel. Counts are
/// scoped to leads DISCOVERED in the briefing week (not "all leads
/// touched this week" — that would conflate weeks and confuse trend reading).
/// StaleClaimedAllTime is the deliberate exception — see field doc.
///
/// Status counts mirror the LeadStatus enum:
///   New | Claimed | Contacted | Joined | Declined | NoResponse | Skipped
/// Sum of all status counts equals TotalSurfaced (every lead has exactly
/// one status at briefing time).
/// </summary>
/// <param name="TotalSurfaced">Total leads inserted into RedditLeads in the briefing week.</param>
/// <param name="StillNew">Surfaced this week, no officer has claimed yet — still sitting in the channel.</param>
/// <param name="Claimed">Officer claimed but hasn't recorded a contact outcome yet.</param>
/// <param name="Contacted">Officer claimed and reported reaching out to the redditor.</param>
/// <param name="Joined">Lead converted to a Discord member (officer-recorded; not auto-detected).</param>
/// <param name="Declined">Lead reached out, said no thanks.</param>
/// <param name="NoResponse">Lead reached out, never heard back.</param>
/// <param name="Skipped">Officer flagged the post as not worth pursuing — useful signal on matcher tuning.</param>
/// <param name="BySubreddit">Top N producers in the briefing week, capped in the section helper. Empty when no leads.</param>
/// <param name="StaleClaimedAllTime">
/// All-time count of leads currently in Claimed status that were discovered
/// more than StaleClaimedDaysThreshold days ago. Cross-week scope on purpose
/// — the briefing is the right place to nudge officers about leads they
/// claimed weeks ago and forgot about.
/// </param>
public sealed record RedditLeadsSnapshot(
    int TotalSurfaced,
    int StillNew,
    int Claimed,
    int Contacted,
    int Joined,
    int Declined,
    int NoResponse,
    int Skipped,
    IReadOnlyList<RedditLeadSubredditBreakdown> BySubreddit,
    int StaleClaimedAllTime);

/// <param name="Subreddit">Subreddit name without the "r/" prefix, as stored on RedditLead.</param>
/// <param name="SurfacedCount">Leads surfaced from this sub in the briefing week.</param>
public sealed record RedditLeadSubredditBreakdown(
    string Subreddit,
    int SurfacedCount);