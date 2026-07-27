namespace ClanGuardBot.Models;

/// <summary>
/// One timestamped reading of a SINGLE power circuit, written on every alert
/// poll alongside the grid-wide <see cref="SatisfactoryMetricSample"/>.
///
/// ── Why both tables exist ──
/// They answer different questions and neither can answer the other's.
/// SatisfactoryMetricSample is deliberately aggregate — circuits get built,
/// merged and dismantled, so per-circuit lines on a 30-day chart silently
/// change meaning, and a chart that lies is worse than one that generalises.
/// But "circuit 3 blew a fuse" needs circuit 3's numbers, and the aggregate
/// row can only say how many circuits were tripped, not which or why.
///
/// ── Why it can't be derived from the live API ──
/// A blown fuse takes everything on the circuit offline, so getPower reports
/// that circuit's PowerConsumed at roughly zero from the moment it trips. The
/// draw that CAUSED the trip exists nowhere in the API — only in whatever was
/// recorded before it. That recording is this table's entire purpose.
///
/// ── Volume ──
/// circuits × one row per poll. At the default 120s cadence and a handful of
/// circuits that's a few thousand rows a day, pruned on the same
/// <see cref="BotConfig.SatisfactoryMetricsRetentionDays"/> window as the
/// aggregate table.
/// </summary>
public class SatisfactoryCircuitSample
{
    public int Id { get; set; }

    public DateTime SampledUtc { get; set; }

    /// <summary>
    /// World seed, so a save wipe doesn't splice two unrelated factories
    /// together. 0 when getSessionInfo wasn't available for this sample.
    /// </summary>
    public long Seed { get; set; }

    /// <summary>
    /// FRM's CircuitGroupID. <b>Not stable across rebuilds</b> — merging two
    /// circuits or dismantling a switch can reassign it. Fine for "what was
    /// this circuit doing ten minutes ago", wrong for long-range trending.
    /// </summary>
    public int CircuitGroupId { get; set; }

    /// <summary>Actual draw at sample time, MW.</summary>
    public double ConsumedMw { get; set; }

    /// <summary>Generating capacity on the circuit, MW.</summary>
    public double CapacityMw { get; set; }

    /// <summary>
    /// FRM's PowerMaxConsumed: what everything on the circuit would draw if it
    /// all ran at once, MW. <b>Not a historical peak.</b> Its diagnostic value
    /// is the comparison with <see cref="CapacityMw"/> — a circuit whose max
    /// possible draw exceeds its capacity is over-subscribed by construction
    /// and will trip again whenever enough machines happen to run together.
    /// </summary>
    public double MaxConsumedMw { get; set; }

    /// <summary>
    /// Reported generation, MW — what this circuit's generators are ACTUALLY
    /// making, versus <see cref="CapacityMw"/>, which is what they could make if
    /// fully supplied. A persistent gap between the two means starved
    /// generators, and explains a trip that the capacity figure says shouldn't
    /// have happened.
    /// </summary>
    public double ProductionMw { get; set; }

    /// <summary>Fuse state at sample time. Rows with this set are the aftermath,
    /// not the cause, and the trip diagnosis filters them out.</summary>
    public bool FuseTriggered { get; set; }

    /// <summary>Battery charge, or null when this circuit has no batteries.</summary>
    public double? BatteryPercent { get; set; }
}
