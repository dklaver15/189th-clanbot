using ClanGuardBot.Models;

namespace ClanGuardBot.Services;

/// <summary>
/// Pure helpers shared by everything that reads FRM's rail endpoints: the alert
/// watch, the /satisfactory-trains command and the map renderer.
///
/// ── Why these are here and not on the records ──
/// The FRM records in <see cref="FrmApiService"/> are deliberately a faithful
/// transcription of the wire format, guesses and all. Interpretation belongs
/// somewhere it can be corrected in one place once someone captures a real
/// response. Three callers each deciding for themselves what "stopped" means is
/// how a chart and an alert end up disagreeing about the same instant.
/// </summary>
public static class SatisfactoryRail
{
    /// <summary>
    /// Below this, a train counts as stationary.
    ///
    /// <para>Not zero. FRM reports speed as a float off a physics body, so a
    /// parked train reads as a small non-zero drift rather than an exact 0, and
    /// a <c>== 0</c> test would mean the stopped-train alert never fires at all.
    /// Half a km/h is far below anything a moving train shows.</para>
    /// </summary>
    public const double StationarySpeedKph = 0.5;

    /// <summary>
    /// Stable-ish key for one train.
    ///
    /// <para>Prefers the actor id, which survives a rename. Falls back to the
    /// name, which does not survive a rebuild. Either way this is scratch state
    /// for "have we already alerted about this one", never something to persist:
    /// a save wipe reassigns both.</para>
    /// </summary>
    public static string KeyOf(FrmTrain train) =>
        !string.IsNullOrWhiteSpace(train.Id) ? train.Id!
        : !string.IsNullOrWhiteSpace(train.Name) ? train.Name!
        : "unnamed-train";

    /// <summary>
    /// What to call a train in a message. Unnamed trains are common (the game
    /// does not force a name), so this must never render as blank.
    /// </summary>
    public static string DisplayName(FrmTrain train) =>
        string.IsNullOrWhiteSpace(train.Name) ? "Unnamed train" : train.Name!.Trim();

    public static string DisplayName(FrmTrainStation station) =>
        string.IsNullOrWhiteSpace(station.Name) ? "Unnamed station" : station.Name!.Trim();

    /// <summary>Speed regardless of direction: a train reversing is still moving.</summary>
    public static double SpeedKph(FrmTrain train) => Math.Abs(train.ForwardSpeed);

    public static bool IsStationary(FrmTrain train) => SpeedKph(train) < StationarySpeedKph;

    /// <summary>
    /// Whether this train is meant to be driving itself around a route.
    ///
    /// <para>The stopped-train alert is gated on this, because a manually-driven
    /// train parked in a siding is not a fault and reporting it as one is how a
    /// feed gets muted. Two or more timetable stops is the signal: one stop is
    /// not a route, and no timetable at all is a train someone drives.</para>
    /// </summary>
    public static bool HasRoute(FrmTrain train) =>
        train.TimeTable is { Count: >= 2 };

    /// <summary>
    /// The next stop, read off the timetable by index rather than from
    /// <c>TrainStation</c>.
    ///
    /// <para>FRM documents <c>TrainStation</c> as "current OR next stop", which
    /// is exactly the ambiguity a stuck-train message must not inherit: for a
    /// stopped train the reader needs to know where it was HEADED. The index
    /// resolves that, and falls back to the ambiguous field only when the
    /// timetable is missing.</para>
    /// </summary>
    public static string? NextStop(FrmTrain train)
    {
        var table = train.TimeTable;

        if (table is { Count: > 0 }
            && train.TimeTableIndex >= 0
            && train.TimeTableIndex < table.Count)
        {
            var name = table[train.TimeTableIndex].StationName;
            if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();
        }

        var fallback = (train.TrainStation ?? string.Empty).Trim();

        // "No Station" is FRM's sentinel for a train with nowhere to be, not the
        // name of a place. Confirmed on the live server 2026-08-09. Without this
        // the roll call reads "next: No Station", which looks like a station
        // somebody named badly.
        if (fallback.Length == 0 || fallback.Equals("No Station", StringComparison.OrdinalIgnoreCase))
            return null;

        return fallback;
    }

    /// <summary>
    /// A diagnostic code worth showing, or null.
    ///
    /// <para>FRM's docs call Status / SelfDriving / Docking / Path "codes" and
    /// then never enumerate them. The live server (2026-08-09) returns them
    /// <b>enum-prefixed</b>: <c>SDLE_NoError</c>, <c>TDS_None</c>,
    /// <c>PDE_NoError</c>. So the healthy-sentinel test runs against the part
    /// AFTER the prefix, or a healthy train would report three faults.</para>
    ///
    /// <para><b>The prefix is stripped for the TEST only; the original string is
    /// what gets returned.</b> A real fault code is the exact thing somebody
    /// will search for, and handing them a truncated <c>NoPath</c> instead of
    /// <c>SDLE_NoPath</c> costs them the search. Nothing branches on a specific
    /// value either way: guessing which unknown codes are benign is how a real
    /// fault gets swallowed.</para>
    /// </summary>
    public static string? Code(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        if (s.Length == 0) return null;

        // Enum prefixes are an uppercase tag and an underscore. Split on the
        // LAST underscore so a hypothetical multi-word tail survives intact.
        var underscore = s.LastIndexOf('_');
        var tail = underscore >= 0 && underscore < s.Length - 1 ? s[(underscore + 1)..] : s;

        return tail.Replace(" ", "").ToLowerInvariant() switch
        {
            "none" or "noerror" or "ok" or "null" or "0" => null,
            _ => s,
        };
    }

    /// <summary>
    /// True when this platform actually moves cargo.
    ///
    /// <para>A station's <c>CargoInventory</c> lists every platform attached to
    /// it, INCLUDING the decorative ones: the live server reports
    /// <c>"LoadingMode": "Empty Platform"</c> for an Empty Platform With
    /// Catwalk (confirmed 2026-08-09). Those have no buffer and must not appear
    /// on the station board, or a station with two catwalks reads as two
    /// starved platforms forever.</para>
    ///
    /// <para>Discriminating on LoadingMode rather than ClassName because the
    /// mode is the thing the board's verdicts already depend on, and there is
    /// more than one cargo platform class (solid and fluid).</para>
    /// </summary>
    public static bool IsCargoPlatform(FrmStationPlatform platform) =>
        (platform.LoadingMode ?? string.Empty).Contains("load", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How full a platform is, 0 to 1, or null when the question doesn't apply.
    ///
    /// <para><b>An empty cargo platform reports an EMPTY Inventory array, not a
    /// stack of zero.</b> That is the single most important thing this method
    /// knows, and it was wrong until the live capture on 2026-08-09: treating
    /// "no stacks" as "unknown" meant a completely starved loading platform,
    /// which is the exact condition the station board exists to catch, was
    /// silently skipped. A cargo platform with nothing in it is 0% full.</para>
    ///
    /// <para>Null is reserved for platforms where fill is genuinely meaningless:
    /// a non-cargo platform, or a stack list with no capacity to divide by.</para>
    /// </summary>
    public static double? Fill(FrmStationPlatform platform)
    {
        if (!IsCargoPlatform(platform)) return null;

        var items = platform.Inventory;
        if (items is null || items.Count == 0) return 0;

        var max = items.Sum(i => i.MaxAmount);
        if (max <= 0) return null;

        return Math.Clamp(items.Sum(i => i.Amount) / max, 0, 1);
    }

    /// <summary>
    /// True when this platform loads trains (as opposed to unloading them).
    ///
    /// <para>The mode decides what a full buffer MEANS, which is the whole point
    /// of the station board: full-and-loading is "no train came", full-and-
    /// unloading is "nothing downstream is eating it". Live values are
    /// "Loading" and "Unloading" (2026-08-09); anything unrecognised is treated
    /// as loading, which is the more common and less alarming reading.</para>
    ///
    /// <para>Only meaningful for a platform that passes
    /// <see cref="IsCargoPlatform"/>.</para>
    /// </summary>
    public static bool IsLoading(FrmStationPlatform platform) =>
        !(platform.LoadingMode ?? string.Empty).Contains("unload", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A world point for a rail segment as a list of x/y pairs, spline first.
    ///
    /// <para>Falls back to the two endpoints when there is no spline, and to the
    /// origin alone when there are no endpoints either. A straight rail may
    /// legitimately report no spline, and dropping those segments would leave
    /// gaps in the map exactly where the network is simplest.</para>
    /// </summary>
    public static IReadOnlyList<FrmLocation> Points(FrmRailSegment rail)
    {
        if (rail.SplineData is { Count: >= 2 }) return rail.SplineData;

        var ends = new List<FrmLocation>(2);
        if (rail.Location0 is not null) ends.Add(rail.Location0);
        if (rail.Location1 is not null) ends.Add(rail.Location1);
        if (ends.Count == 2) return ends;

        return rail.Location is not null ? new[] { rail.Location } : Array.Empty<FrmLocation>();
    }

    /// <summary>
    /// The length of one rail segment in world units, measured from its own
    /// geometry rather than read from FRM's <c>Length</c> field.
    ///
    /// <para><b>Why not just trust Length.</b> The 2026-08-09 capture of
    /// getTrainRails shows ID, Name, ClassName, location, BoundingBox,
    /// ColorSlot and SplineData, and no <c>Length</c> among them (the capture
    /// was truncated before the end of the first object, so the field may exist
    /// further down and may not). Summing the spline needs no such assumption:
    /// SplineData is the one thing that response definitely has, and it is
    /// already what the map draws. Length is preferred when it turns up
    /// non-zero, so a real value still wins.</para>
    ///
    /// <para>Chord-summing a curve slightly under-measures it, which does not
    /// matter here: the captured points sit under a metre apart, so the error
    /// is far below the precision of the "N km of track" line this feeds.</para>
    /// </summary>
    public static double LengthUnits(FrmRailSegment rail)
    {
        if (rail.Length > 0) return rail.Length;

        var points = rail.SplineData;
        if (points is null || points.Count < 2) return 0;

        var total = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            var dz = points[i].Z - points[i - 1].Z;

            var step = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (double.IsFinite(step)) total += step;
        }

        return total;
    }
}
