namespace ClanGuardBot.Models;

/// <summary>
/// A once-a-day photograph of the factory's headline numbers, written by the
/// daily digest just before it posts.
///
/// ── Why this exists ──
/// FRM is a LIVE API. getPower tells you the draw right now; getResourceSink
/// tells you the point total right now. Nothing in the mod — and, before this
/// table, nothing in the bot — remembered what those numbers were yesterday. So
/// the digest could say "1.2M sink points" but never "up 340k since yesterday",
/// which is the sentence people actually care about. One row per digest turns a
/// series of snapshots into a trend.
///
/// ── Why it's this small ──
/// Deliberately aggregates, not a per-item production dump. getProdStats lists
/// every item the save knows about (575 on the clan's server), and storing all
/// of them daily would grow a table nobody queries in order to answer a question
/// nobody asked. Everything here is a number the digest actually renders.
///
/// ── Seed scoping ──
/// Same rule as <see cref="SatisfactoryUnlock"/>: rows belong to a world. After
/// a save wipe the new seed has no history, so the digest quietly shows no
/// deltas for a day instead of reporting that the clan lost 1.2M sink points
/// overnight.
///
/// ── Nullable columns ──
/// Every figure is nullable because the digest is best-effort: if getPower
/// answered but getResourceSink timed out, we still want the power figures
/// recorded rather than a row of zeroes that later reads as "the sink was empty
/// on Tuesday". Null means "not measured", which is a different fact from zero.
/// </summary>
public class SatisfactoryDailySnapshot
{
    public int Id { get; set; }

    /// <summary>World seed from getSessionInfo. Scopes the whole row to one save.</summary>
    public long Seed { get; set; }

    /// <summary>When the snapshot was taken (digest time, ~9am Central).</summary>
    public DateTime TakenUtc { get; set; }

    /// <summary>
    /// The local calendar day the digest was REPORTING on — i.e. yesterday, not
    /// the day it posted. Stored as a plain date string (yyyy-MM-dd) so lookups
    /// don't have to redo the timezone maths, and so two digests can't both
    /// claim the same reporting day.
    /// </summary>
    public string LocalDate { get; set; } = string.Empty;

    /// <summary>In-game days elapsed, from getSessionInfo.</summary>
    public int PassedDays { get; set; }

    // ── Power ──
    public double? PowerCapacityMw { get; set; }
    public double? PowerConsumedMw { get; set; }
    public int? CircuitCount { get; set; }

    // ── AWESOME Sink ──
    public long? SinkTotalPoints { get; set; }
    public int? SinkCoupons { get; set; }

    // ── Production ──
    /// <summary>How many distinct items were actually producing (&gt; 0.01/min).</summary>
    public int? ProducingItemCount { get; set; }

    /// <summary>Sum of current output across everything producing, items/min.</summary>
    public double? TotalProductionPerMin { get; set; }
}
