namespace ClanGuardBot.Briefing;

/// <summary>
/// Pre-aggregated, pre-filtered weekly snapshot sent to Claude.
///
/// COST NOTE: Every field here was filtered or aggregated in C# before
/// reaching the prompt. We send a few dozen line items, NOT the full roster
/// + attendance log. With the v2 enrichments (per-item activity context,
/// spotlight, risk watch, week-over-week deltas) target serialized size is
/// ~1.5K input tokens, up from ~600 in v1. Per-run cost lands around $0.02.
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

    /// <summary>Free-form short notes for things that don't fit the other buckets.</summary>
    public IReadOnlyList<string> Anomalies { get; init; } = [];
}

public sealed record AwolRiskItem(
    string Gamertag,
    string CurrentRank,
    /// <summary>Days since the AWOL role was assigned.</summary>
    int DaysSinceAwolAssigned,
    /// <summary>Messages sent in the last 14 days. 0 = fully checked out.</summary>
    int Messages14d,
    /// <summary>Voice hours in the last 14 days. Capped per session per BotConfig.MaxSingleSessionHours.</summary>
    double VoiceHours14d,
    /// <summary>One-line context, e.g. "flagged 5d ago, grace period elapsed".</summary>
    string TrendNote);

public sealed record PromotionCandidate(
    string Gamertag,
    string CurrentRank,
    string ProposedRank,
    /// <summary>Cumulative events at current rank (bot-tracked + seed). Used by promotion math directly.</summary>
    int EventsAttendedAtRank,
    /// <summary>Messages sent since rank was assigned.</summary>
    int MessagesAtRank,
    /// <summary>Voice hours since rank was assigned (capped per session).</summary>
    double VoiceHoursAtRank,
    int DaysInRank);

public sealed record NotableEvent(
    string EventName,
    DateTime Date,
    int Attendance,
    /// <summary>"Clan" or "CompDiv" — matches the existing event source filter.</summary>
    string Source);

/// <summary>
/// Member trending toward AWOL: not yet flagged, but below the activity
/// threshold on at least one axis with the closest ratio between 50% and 100%.
/// "Below 50%" is excluded as those members are about to be flagged AWOL by
/// AwolCheckService anyway and would clutter this section.
/// </summary>
public sealed record RiskWatchItem(
    string Gamertag,
    string CurrentRank,
    int Messages,
    double VoiceHours,
    int RequiredMessages,
    double RequiredVoiceHours,
    int WindowDays,
    /// <summary>Closest threshold ratio reached (0.5–1.0). Higher = closer to safe.</summary>
    double ClosestThresholdRatio);

public sealed record MemberSpotlight(
    string Gamertag,
    string CurrentRank,
    int Messages,
    double VoiceHours,
    int EventsAttended,
    /// <summary>Combined score (1pt/msg + 5pts/voice hour + 10pts/event). Tunable in BriefingDataCollector.</summary>
    int Score);

/// <summary>
/// Week-over-week deltas. Negative numbers mean "worse than last week"
/// (fewer events, lower attendance, fewer recruits). Null fields mean the
/// prior week's data couldn't be computed (e.g. brand-new database).
/// </summary>
public sealed record WeekOverWeekDeltas(
    int EventsHeldDelta,
    /// <summary>Percentage-point change, e.g. -12.5 means attendance dropped 12.5pp vs last week.</summary>
    double AverageAttendancePercentDelta,
    int NewRecruitsDelta);