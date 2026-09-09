namespace ClanGuardBot.Models;

/// <summary>
/// One timestamped A2S reading of the Valheim server, taken on each poll of
/// <see cref="Services.ValheimStatusService"/> while the server is reachable.
///
/// ── Why store these ──
/// A2S is instantaneous: it says how many people are on right now and remembers
/// nothing. That answers "is anyone playing?" but not the questions that actually
/// come up when planning a wipe or a raid night — which evenings the server is
/// busy, whether it is trending up or dying, and whether the 10-slot Shockbyte
/// plan is ever the constraint. Those need history, and nothing else records it.
///
/// ── Deliberately thin ──
/// Only what A2S actually returns. There are no FPS or tick-rate columns like
/// <see cref="PalworldMetricSample"/> has, because vanilla Valheim exposes no
/// performance counters over the query protocol — that would need a server-side
/// mod. Recording a placeholder would imply a signal that does not exist.
///
/// ── Volume ──
/// Append-only, pruned on <see cref="BotConfig.ValheimMetricsRetentionDays"/>. At
/// the default 60s cadence that is ~1,440 rows/day, so a 30-day window is ~43,000
/// rows — the same order as the Palworld table, which SQLite has handled since July.
/// </summary>
public class ValheimMetricSample
{
    public int Id { get; set; }

    public DateTime SampledUtc { get; set; }

    /// <summary>Players connected at sample time.</summary>
    public int PlayerCount { get; set; }

    /// <summary>
    /// Slot limit the server reported. Stored per-row rather than assumed, so a
    /// plan upgrade is visible in the history instead of silently rewriting it.
    /// </summary>
    public int MaxPlayerCount { get; set; }

    /// <summary>
    /// World name at sample time. A wipe or a swap to a different save changes
    /// this, which is what stops two unrelated worlds being plotted as one line.
    /// </summary>
    public string World { get; set; } = string.Empty;

    /// <summary>
    /// Query round-trip in milliseconds. Not server tick rate — it measures the
    /// network path and how promptly the query thread answered, so it is a rough
    /// health hint only, useful mainly for spotting a host that has gone sick
    /// rather than down.
    /// </summary>
    public int RoundTripMs { get; set; }
}
