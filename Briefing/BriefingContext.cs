namespace ClanGuardBot.Briefing;

/// <summary>
/// Pre-aggregated, pre-filtered weekly snapshot sent to Claude.
/// 
/// COST NOTE: This shape is the single biggest lever on briefing cost.
/// Every field here represents a filtering or aggregation step done in C#
/// to keep the prompt small. We send a few dozen line items, NOT the full
/// roster + attendance log. Target serialized size: ~3K input tokens.
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

    // Attention items — pre-filtered to top N each, capped in the collector
    public IReadOnlyList<AwolRiskItem> AwolRisks { get; init; } = [];
    public IReadOnlyList<PromotionCandidate> PromotionCandidates { get; init; } = [];
    public IReadOnlyList<NotableEvent> NotableEvents { get; init; } = [];

    /// <summary>Free-form short notes for things that don't fit the other buckets.</summary>
    public IReadOnlyList<string> Anomalies { get; init; } = [];
}

public sealed record AwolRiskItem(
    string Gamertag,
    string CurrentRank,
    int DaysSinceLastActivity,
    /// <summary>One-line context, e.g. "missed last 3 ops, no Discord activity 12d".</summary>
    string TrendNote);

public sealed record PromotionCandidate(
    string Gamertag,
    string CurrentRank,
    string ProposedRank,
    int EventsAttendedLast30Days,
    int DaysInRank);

public sealed record NotableEvent(
    string EventName,
    DateTime Date,
    int Attendance,
    /// <summary>"CompDiv" or "Clan" — matches your existing event source filter.</summary>
    string Source);