namespace ClanGuardBot.Models;

/// <summary>
/// One timestamped snapshot of the Palworld server's health, taken on each
/// presence poll (default every 60s) while the server is reachable.
///
/// ── Why store these ──
/// "The server feels laggy when a lot of people are on" isn't actionable. Palworld
/// has two well-known and very different performance failure modes, and they need
/// opposite fixes:
///   • a single-threaded simulation bottleneck, which scales with PLAYER COUNT and
///     with how many worker Pals are being simulated; and
///   • a memory leak, where the process degrades the longer it has been UP,
///     regardless of load.
/// Recording FPS alongside both the player count and the server's uptime lets
/// /palworld-performance bucket the samples and show which one actually correlates
/// — i.e. whether the answer is "cut BaseCampWorkerMaxNum" or "restart more often".
///
/// Append-only and pruned on a retention window
/// (<see cref="BotConfig.PalworldMetricsRetentionDays"/>); at a 60s cadence this is
/// ~1,440 rows/day, so a 30-day window is a trivially small table.
/// </summary>
public class PalworldMetricSample
{
    public int Id { get; set; }

    public DateTime SampledUtc { get; set; }

    /// <summary>Server tick rate. ~60 is healthy; sustained drops are the lag signal.</summary>
    public int ServerFps { get; set; }

    /// <summary>Milliseconds per frame. The inverse view of FPS, kept as reported.</summary>
    public double FrameTimeMs { get; set; }

    /// <summary>Players connected at sample time — the load axis.</summary>
    public int PlayerCount { get; set; }

    public int MaxPlayerCount { get; set; }

    /// <summary>
    /// Seconds since the server process started. The memory-leak axis: bucketing FPS
    /// by this shows whether performance decays with time-since-restart, which is
    /// what justifies (or rules out) a shorter restart cadence.
    /// </summary>
    public int UptimeSeconds { get; set; }

    /// <summary>Base camps on the server — a proxy for how much is being simulated.</summary>
    public int BaseCampCount { get; set; }

    public int InGameDay { get; set; }
}
