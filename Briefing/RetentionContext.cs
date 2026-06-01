namespace ClanGuardBot.Briefing;

/// <summary>
/// Retention snapshot for the briefing week, sourced from the MemberDeparture
/// table via BriefingRetentionSection. Attached to BriefingContext as a
/// nullable property; null means zero departures, and the prompt treats null
/// as "skip the section" — same convention as Spotlight / RedditLeads.
///
/// Counts sum: Voluntary + KickedManual + KickedByBot + Banned == TotalDepartures.
/// Tenure stats are computed over DeparturesWithKnownTenure only (departures
/// whose join date couldn't be resolved are excluded from the median and the
/// buckets, but still counted in TotalDepartures).
/// </summary>
/// <param name="TotalDepartures">All departures in the week, any classification.</param>
/// <param name="Voluntary">Classification == "Left".</param>
/// <param name="KickedManual">Classification == "Kicked" (officer kick).</param>
/// <param name="KickedByBot">"KickedAwol" + "KickedAccountAge" — automated removals, not member loss.</param>
/// <param name="Banned">Classification == "Banned".</param>
/// <param name="NetMembershipChange">NewRecruits - TotalDepartures for the week. Negative = the server shrank.</param>
/// <param name="DeparturesWithKnownTenure">How many of TotalDepartures had a resolvable join date.</param>
/// <param name="MedianTenureDays">Median tenure over known-tenure departures. Null if none had a known join date.</param>
/// <param name="TenureBuckets">Distribution across fixed buckets, over known-tenure departures.</param>
/// <param name="SameWeekChurn">Joined AND left within the briefing week — instant churn.</param>
/// <param name="Rejoiners">Departures flagged IsRejoin — boomerang members leaving again.</param>
/// <param name="GuestChurn">Departures where the member never progressed past Guest (onboarding leakage).</param>
/// <param name="EngagedChurn">Departures with at least one attended event — real members we lost.</param>
/// <param name="ReconciledDepartures">Departures detected after the fact by the roster reconciler (bot was offline). Their timing is approximate.</param>
/// <param name="NotableDepartures">Veterans / ranked / engaged members worth a human glance. Capped.</param>
/// <param name="ChurnBySource">Departures grouped by the invite label they originally joined under. Capped.</param>
public sealed record RetentionSnapshot(
    int TotalDepartures,
    int Voluntary,
    int KickedManual,
    int KickedByBot,
    int Banned,
    int NetMembershipChange,
    int DeparturesWithKnownTenure,
    double? MedianTenureDays,
    IReadOnlyList<TenureBucket> TenureBuckets,
    int SameWeekChurn,
    int Rejoiners,
    int GuestChurn,
    int EngagedChurn,
    int ReconciledDepartures,
    IReadOnlyList<NotableDeparture> NotableDepartures,
    IReadOnlyList<ChurnBySourceItem> ChurnBySource);

/// <param name="Label">"&lt;24h" | "1-7d" | "8-30d" | "31-90d" | "90d+".</param>
/// <param name="Count">Departures whose tenure fell in this bucket.</param>
public sealed record TenureBucket(string Label, int Count);

/// <summary>A departure worth calling out by name — veteran, ranked, or engaged.</summary>
/// <param name="TenureDays">Null when the join date couldn't be resolved.</param>
/// <param name="Classification">"Left" | "Kicked" | "KickedAwol" | "KickedAccountAge" | "Banned".</param>
public sealed record NotableDeparture(
    string DisplayName,
    string RankAtDeparture,
    double? TenureDays,
    string Classification,
    int EventsAttendedLifetime);

/// <param name="Label">The InviteJoin.LabelSnapshot the departed member originally joined under.</param>
/// <param name="Departures">How many of the week's departures came in via this label.</param>
public sealed record ChurnBySourceItem(string Label, int Departures);
