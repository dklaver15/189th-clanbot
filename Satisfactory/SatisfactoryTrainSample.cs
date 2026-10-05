namespace ClanGuardBot.Models;

/// <summary>
/// One timestamped reading of the whole rail network, written on the train poll
/// alongside the power samples.
///
/// ── Why aggregate and not per train ──
/// Same reasoning as <see cref="SatisfactoryMetricSample"/> against
/// <see cref="SatisfactoryCircuitSample"/>, but the conclusion lands differently.
/// Trains get renamed, rebuilt and re-consisted, and FRM's train id is an actor
/// id that a rebuild replaces. A per-train series would therefore change meaning
/// under its own line, and nothing here needs to read one train's history: the
/// alerting works off live state, and the chart is a network-level question
/// ("are we moving more freight than last week"). So one row per poll.
///
/// ── What PayloadMass actually measures ──
/// The sum of what every train is CARRYING at the instant of the sample, not
/// what has been delivered. It is a snapshot of freight in flight, so it rises
/// as trains load and falls as they unload, and the useful signal is the trend
/// and the ceiling rather than any single reading. FRM exposes no delivered-total
/// counter, and integrating unload events out of a poll would invent precision
/// the data does not have.
///
/// ── Volume ──
/// One row per alert poll. At the default 120s cadence that is ~720 rows/day,
/// pruned on the same <see cref="BotConfig.SatisfactoryMetricsRetentionDays"/>
/// window as the power tables.
/// </summary>
public class SatisfactoryTrainSample
{
    public int Id { get; set; }

    public DateTime SampledUtc { get; set; }

    /// <summary>
    /// World seed, so a save wipe doesn't splice two unrelated networks into one
    /// chart line. 0 when getSessionInfo wasn't available for this sample.
    /// </summary>
    public long Seed { get; set; }

    /// <summary>Trains FRM reported, derailed ones included.</summary>
    public int TrainCount { get; set; }

    /// <summary>Trains with a non-trivial forward speed at sample time.</summary>
    public int MovingCount { get; set; }

    /// <summary>Trains reporting Derailed at sample time.</summary>
    public int DerailedCount { get; set; }

    /// <summary>
    /// Trains the watch currently considers stuck: stationary for longer than
    /// the configured window, or reporting a self-driving or pathing code.
    /// Recorded rather than recomputed so the chart and the alerts can never
    /// disagree about the same instant.
    /// </summary>
    public int StuckCount { get; set; }

    /// <summary>
    /// Total payload across every train, in FRM's own mass units. See the class
    /// remarks: freight in flight, not freight delivered.
    /// </summary>
    public double PayloadMass { get; set; }

    /// <summary>
    /// Total payload CAPACITY across every train. The pair gives a load factor
    /// that is unit-free, which matters because FRM never says what the mass
    /// unit is.
    /// </summary>
    public double MaxPayloadMass { get; set; }

    /// <summary>Stations seen on this poll, or null when stations weren't read.</summary>
    public int? StationCount { get; set; }

    /// <summary>
    /// Loading platforms sitting near empty: the factory feeding them isn't
    /// keeping up. Null when stations weren't read on this poll.
    /// </summary>
    public int? StarvedPlatforms { get; set; }

    /// <summary>
    /// Platforms sitting near full: on a loading platform no train is
    /// collecting, on an unloading platform nothing downstream is consuming.
    /// Null when stations weren't read on this poll.
    /// </summary>
    public int? BackedUpPlatforms { get; set; }
}
