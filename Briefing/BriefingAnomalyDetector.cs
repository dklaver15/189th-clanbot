using ClanGuardBot.Models;

namespace ClanGuardBot.Briefing;

/// <summary>
/// Deterministic anomaly detection over the briefing-week snapshot.
///
/// ── What this is (and is not) ──
/// Not an alerting system. Not a statistical model. Not a second LLM pass.
/// A small set of threshold rules over the same data the briefing already
/// has, producing 0..N short factual strings (e.g. "Events dropped from 4
/// to 1 this week (-75%)"). The output drops into BriefingContext.Anomalies
/// and the existing weekly-briefing AI call prose-ifies it into the Notable
/// section.
///
/// ── Why deterministic instead of "AI pass" #2 ──
/// Dan's brief was "AI pass that flags unusual patterns" — the natural read
/// is a second LLM call. We chose deterministic + single-pass enrichment
/// instead because:
///   1. Cost. Two LLM calls double the per-week spend for marginal value.
///   2. Stability. The same week always produces the same anomalies, which
///      makes regressions in this code visible. A second LLM call would
///      drift week-over-week.
///   3. Auditability. When an officer asks "why did the briefing flag
///      this?" the answer is a threshold from this file, not "Claude
///      decided." We can tune the thresholds; we can't tune a black box.
/// The trade-off: we will miss patterns we didn't think to check for. The
/// existing briefing prompt already lets Claude surface anything weird it
/// notices in Notable, so we get some discovery for free without paying for
/// a second call.
///
/// ── Rules (all thresholds tuned for 189th roster size ~125 active) ──
///   1. Events: drop ≥ 2 absolute AND ≥ 50% relative
///   2. Events: surge of ≥ 100% relative (and absolute ≥ +2)
///   3. Attendance: change ≥ ±15 percentage points
///   4. Recruits: surge of ≥ +5 absolute
///   5. Recruits: drought ≥ -5 absolute AND prior week had ≥ 3
///   6. Single-day join clustering: one day has ≥ 50% of weekly joins
///      AND ≥ 3 absolute joins on that day AND ≥ 5 weekly joins
///
/// Tune thresholds in the constants below — the rule logic stays the same.
///
/// Pure static function: no DI, no DB, no logging. Easy to unit-test, easy
/// to read, easy to delete and rewrite.
/// </summary>
internal static class BriefingAnomalyDetector
{
    // Events
    private const int    EventsDropAbsolute  = 2;
    private const double EventsDropRelative  = 0.50;
    private const int    EventsSurgeAbsolute = 2;
    private const double EventsSurgeRelative = 1.00;

    // Attendance (percentage points)
    private const double AttendanceDeltaPp = 15.0;

    // Recruits
    private const int RecruitsSurgeAbsolute   = 5;
    private const int RecruitsDroughtAbsolute = 5;
    private const int RecruitsDroughtPriorMin = 3;

    // Join clustering (single-day spike)
    private const double JoinClusterShareThreshold = 0.50;
    private const int    JoinClusterDayMin         = 3;
    private const int    JoinClusterWeekMin        = 5;

    // Bound the output — the briefing already has tight token budget;
    // dumping 10 anomalies would drown the Notable section.
    private const int MaxAnomalies = 5;

    /// <summary>
    /// Run all rules and return the surfaced anomalies. Each string is a
    /// short factual statement intended to be dropped verbatim into the
    /// Notable section, or paraphrased by the AI when it has additional
    /// context to weave in.
    ///
    /// <paramref name="deltas"/> being null means we couldn't compute prior-
    /// week numbers (brand-new DB, or the prior week had no data); in that
    /// case all WoW rules are skipped silently — same as the rest of the
    /// briefing's null-handling convention.
    /// </summary>
    /// <param name="deltas">Week-over-week deltas vs the prior 7-day window.</param>
    /// <param name="eventsHeldThisWeek">Events held in the briefing week (matches BriefingContext.EventsHeld).</param>
    /// <param name="averageAttendancePercentThisWeek">Current week's average event attendance (matches BriefingContext.AverageAttendancePercent).</param>
    /// <param name="newRecruitsThisWeek">New recruits in the briefing week (matches BriefingContext.NewRecruits).</param>
    /// <param name="joinsByDay">
    ///   Map of date → join count for the briefing week. Keys are date-only
    ///   (time portion ignored). May be empty if the week saw zero joins.
    /// </param>
    public static IReadOnlyList<string> Detect(
        WeekOverWeekDeltas? deltas,
        int eventsHeldThisWeek,
        double averageAttendancePercentThisWeek,
        int newRecruitsThisWeek,
        IReadOnlyDictionary<DateTime, int> joinsByDay)
    {
        var anomalies = new List<string>();

        if (deltas is not null)
        {
            // Compute prior-week values by subtracting the deltas from this
            // week. Cleaner than threading prior-week values separately.
            var priorEvents = eventsHeldThisWeek - deltas.EventsHeldDelta;
            var priorRecruits = newRecruitsThisWeek - deltas.NewRecruitsDelta;

            // ── Rule 1: Events drop ──
            if (deltas.EventsHeldDelta <= -EventsDropAbsolute && priorEvents > 0)
            {
                var relativeDrop = (double)Math.Abs(deltas.EventsHeldDelta) / priorEvents;
                if (relativeDrop >= EventsDropRelative)
                {
                    anomalies.Add(
                        $"Events dropped from {priorEvents} to {eventsHeldThisWeek} " +
                        $"({(int)Math.Round(relativeDrop * 100)}% decrease week-over-week)");
                }
            }

            // ── Rule 2: Events surge ──
            if (deltas.EventsHeldDelta >= EventsSurgeAbsolute && priorEvents > 0)
            {
                var relativeRise = (double)deltas.EventsHeldDelta / priorEvents;
                if (relativeRise >= EventsSurgeRelative)
                {
                    anomalies.Add(
                        $"Events surged from {priorEvents} to {eventsHeldThisWeek} " +
                        $"(+{(int)Math.Round(relativeRise * 100)}% week-over-week)");
                }
            }

            // ── Rule 3: Attendance swing ──
            if (Math.Abs(deltas.AverageAttendancePercentDelta) >= AttendanceDeltaPp)
            {
                var direction = deltas.AverageAttendancePercentDelta > 0 ? "rose" : "fell";
                anomalies.Add(
                    $"Average attendance {direction} {Math.Abs(deltas.AverageAttendancePercentDelta):F1}pp " +
                    $"week-over-week (now at {averageAttendancePercentThisWeek:F0}%)");
            }

            // ── Rule 4: Recruit surge ──
            if (deltas.NewRecruitsDelta >= RecruitsSurgeAbsolute)
            {
                anomalies.Add(
                    $"Recruit influx: {newRecruitsThisWeek} new recruits this week " +
                    $"vs {priorRecruits} prior (+{deltas.NewRecruitsDelta})");
            }

            // ── Rule 5: Recruit drought ──
            if (deltas.NewRecruitsDelta <= -RecruitsDroughtAbsolute && priorRecruits >= RecruitsDroughtPriorMin)
            {
                anomalies.Add(
                    $"Recruit drought: {newRecruitsThisWeek} new recruits this week " +
                    $"vs {priorRecruits} prior ({deltas.NewRecruitsDelta})");
            }
        }

        // ── Rule 6: Single-day join clustering ──
        // Independent of WoW; catches "raid-style" join bursts inside the
        // briefing week regardless of how the prior week looked.
        if (joinsByDay.Count > 0)
        {
            var totalJoins = joinsByDay.Values.Sum();
            if (totalJoins >= JoinClusterWeekMin)
            {
                var topDay = joinsByDay
                    .OrderByDescending(kv => kv.Value)
                    .First();
                var share = (double)topDay.Value / totalJoins;
                if (topDay.Value >= JoinClusterDayMin && share >= JoinClusterShareThreshold)
                {
                    anomalies.Add(
                        $"Join clustering on {topDay.Key:MMM d}: {topDay.Value} of {totalJoins} " +
                        $"weekly joins ({(int)Math.Round(share * 100)}%) landed on a single day");
                }
            }
        }

        return anomalies.Count <= MaxAnomalies ? anomalies : anomalies.Take(MaxAnomalies).ToList();
    }
}
