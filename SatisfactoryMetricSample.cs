namespace ClanGuardBot.Models;

/// <summary>
/// One timestamped reading of the factory's power grid, taken on each alert
/// poll (default every 120s) while FRM is reachable.
///
/// ── Why store these ──
/// FRM is a live API: getPower tells you the draw right now and remembers
/// nothing. That's enough to alert on a blown fuse, but it can't answer the
/// question people actually ask after one blows — "was this coming?" A grid
/// creeping from 60% to 95% load over four days is obvious in a chart and
/// invisible in a series of instantaneous readings.
///
/// ── Why it's free ──
/// <see cref="Services.SatisfactoryFactoryService"/> already calls getPower on
/// every alert poll. This records what that call returned, so the sampling adds
/// a row insert and NOT a request to the game server. Nothing here justifies
/// its own poll loop.
///
/// ── Volume ──
/// Append-only, pruned on <see cref="BotConfig.SatisfactoryMetricsRetentionDays"/>.
/// At the default 120s cadence that's ~720 rows/day, so a 30-day window is
/// ~21,600 rows — trivial for SQLite, and the same shape as
/// <see cref="PalworldMetricSample"/>, which has been running at twice this
/// rate since July.
///
/// ── Aggregate, not per-circuit ──
/// One row per poll for the whole grid, not one per circuit. Circuits get
/// rebuilt, merged and dismantled, so their ids aren't stable enough to plot a
/// series against — a chart whose lines silently change meaning is worse than
/// one showing the total. <see cref="TrippedCount"/> keeps the per-circuit
/// detail that actually matters at this resolution.
/// </summary>
public class SatisfactoryMetricSample
{
    public int Id { get; set; }

    public DateTime SampledUtc { get; set; }

    /// <summary>
    /// World seed, so a save wipe doesn't splice two unrelated factories into
    /// one line. 0 when getSessionInfo wasn't available for this sample — the
    /// row is still worth keeping for the power figures.
    /// </summary>
    public long Seed { get; set; }

    /// <summary>Total draw across all circuits, MW.</summary>
    public double PowerConsumedMw { get; set; }

    /// <summary>
    /// Total generating capacity across all circuits, MW. This is the headroom
    /// line on the chart — the one worth watching consumption approach.
    /// </summary>
    public double PowerCapacityMw { get; set; }

    /// <summary>
    /// Reported generation, MW. <b>Reads 0 on this server</b> even while
    /// circuits draw tens of megawatts, which is why the digest doesn't print
    /// it. Recorded anyway so that if a future FRM version starts populating
    /// it, the history is already there rather than starting from zero.
    /// </summary>
    public double PowerProductionMw { get; set; }

    /// <summary>Circuits seen this poll.</summary>
    public int CircuitCount { get; set; }

    /// <summary>
    /// Circuits with a blown fuse at sample time. Drawn as markers on the
    /// chart, which is the whole point of sampling: it puts the trip in the
    /// context of the load that caused it.
    /// </summary>
    public int TrippedCount { get; set; }

    /// <summary>
    /// Mean charge across circuits that actually have batteries, or null when
    /// none do — which is the clan's current state, since nobody has built a
    /// Power Storage. Null rather than 0 so an empty grid is distinguishable
    /// from a flat one.
    /// </summary>
    public double? BatteryPercent { get; set; }
}
