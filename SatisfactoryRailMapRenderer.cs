using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>
/// Draws the clan's rail network as a PNG: the track, the stations, and where
/// every train is right now.
///
/// ── Why the track is cached and the trains are not ──
/// getTrainRails is the heaviest endpoint this bot touches. It returns one
/// object per rail segment, each carrying a spline point list, so a mature
/// network runs to megabytes. It also only changes when somebody builds. Trains
/// move constantly and getTrains is tiny. So the geometry is held for
/// <see cref="BotConfig.SatisfactoryRailMapCacheMinutes"/> and only the markers
/// are re-read, which turns a repeat render into two small requests.
///
/// ── The projection is derived, not assumed ──
/// Nothing here hardcodes the game world's extents. The view is fitted to the
/// bounding box of whatever came back, so it frames the clan's actual network
/// instead of their four stations floating in an ocean of empty map. It also
/// means the render cannot be silently wrong if the world bounds ever differ
/// from what a wiki says.
///
/// <para>Screen x follows world X and screen y follows world Y, both increasing
/// right and down, which is the orientation the in-game map uses. If a render
/// ever comes out mirrored against the game map, <see cref="FlipY"/> is the one
/// line to change: everything else is derived from it.</para>
///
/// ── Failure handling ──
/// Returns null on anything going wrong, already logged. A map is never worth
/// losing the message it was attached to.
/// </summary>
public sealed class SatisfactoryRailMapRenderer
{
    private readonly FrmApiService _frm;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryRailMapRenderer> _logger;

    /// <summary>
    /// Whether world Y grows downward on screen. See the class remarks: this is
    /// the single knob for the map's orientation.
    /// </summary>
    private const bool FlipY = false;

    /// <summary>
    /// Unreal world units per metre. The engine's default is 1 unit = 1 cm and
    /// Satisfactory keeps it (a foundation is 8 m and measures 800 units), which
    /// makes this the only unit conversion in the rail feature. It affects the
    /// scale bar and the track-length figure and nothing else, so a wrong value
    /// here is cosmetic rather than structural.
    /// </summary>
    public const double UnitsPerMetre = 100.0;

    // ── Layout ───────────────────────────────────────────────────────────────
    //
    // Width is fixed; HEIGHT IS DERIVED from the network's own aspect ratio (see
    // Render). A fixed canvas letterboxes badly here in a way it doesn't for a
    // time-series chart: the map holds one scale on both axes to keep the
    // network's real shape, so a long east-west main line on a fixed 4:3 canvas
    // is a thin ribbon with 40% of the image empty.
    private const int CanvasWidth  = 1500;
    private const int MarginLeft   = 60;
    private const int MarginRight  = 60;
    private const int MarginTop    = 96;
    private const int MarginBottom = 76;

    /// <summary>
    /// Bounds on the derived plot height. The floor keeps a very wide network
    /// from rendering as a letterbox slot too short for the legend to clear the
    /// track; the ceiling stops a tall thin one from producing an image Discord
    /// will scale down to illegibility anyway.
    /// </summary>
    private const float MinPlotHeight = 420;

    private const float MaxPlotHeight = 1150;

    // ── Palette ──────────────────────────────────────────────────────────────
    private const string BgTopHex    = "141b28";
    private const string BgBottomHex = "080a10";
    private const string LiftHex     = "2a3550";
    private const string InkHex      = "ffffff";
    private const string RailHex     = "8fa3c8";
    private const string StationHex  = "ffd86b";
    private const string MovingHex   = "00d4ff";

    /// <summary>
    /// Orange, not the amber the stopped-train ALERT embed uses. On the map a
    /// stopped train sits next to a gold station marker, and the two ambers were
    /// indistinguishable at marker size: the first render of this map had a
    /// legend whose "Station" and "Stopped" swatches looked identical. The alert
    /// embed has no such neighbour and keeps its amber.
    /// </summary>
    private const string StoppedHex  = "e67e22";

    private const string DerailedHex = "ed4245";

    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private IReadOnlyList<FrmRailSegment>? _cachedRails;
    private DateTime _railsFetchedUtc = DateTime.MinValue;

    public SatisfactoryRailMapRenderer(
        FrmApiService frm,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryRailMapRenderer> logger)
    {
        _frm = frm;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan RailCacheFor =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryRailMapCacheMinutes, 1, 1440));

    /// <summary>
    /// Renders the map. Returns null when there is nothing to draw or the server
    /// can't be reached.
    /// </summary>
    /// <param name="subtitle">Second header line, e.g. a timestamp or a caption.</param>
    public async Task<byte[]?> TryRenderAsync(string subtitle, CancellationToken ct = default)
    {
        try
        {
            var rails = await GetRailsAsync(ct);

            // Stations and trains are cheap and always fresh. Both are optional:
            // a network under construction has track and no trains, and that is
            // still a map worth looking at.
            var stations = await _frm.GetTrainStationsAsync(ct) ?? Array.Empty<FrmTrainStation>();
            var trains = await _frm.GetTrainsAsync(ct) ?? Array.Empty<FrmTrain>();

            var polylines = (rails ?? Array.Empty<FrmRailSegment>())
                .Select(SatisfactoryRail.Points)
                .Where(p => p.Count >= 2)
                .ToList();

            if (polylines.Count == 0 && stations.Count == 0)
            {
                _logger.LogDebug("Skipping the Satisfactory rail map: no track or stations to draw");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            return Render(polylines, stations, trains, subtitle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render the Satisfactory rail map");
            return null;
        }
    }

    /// <summary>
    /// Total built track, in metres, from the cached geometry. Null when the
    /// geometry isn't available. Used by the daily digest, which must not
    /// trigger a fetch of the heaviest endpoint on its own account.
    /// </summary>
    public double? CachedTrackMetres()
    {
        var rails = _cachedRails;
        if (rails is null || rails.Count == 0) return null;

        return rails.Sum(SatisfactoryRail.LengthUnits) / UnitsPerMetre;
    }

    /// <summary>
    /// The cached track geometry, refetched when stale.
    ///
    /// <para>Guarded by a semaphore rather than a lock because the fetch is
    /// async. Without it, two commands issued together would both miss the cache
    /// and both pull the heaviest endpoint on the server at the same moment,
    /// which is exactly the load this cache exists to prevent.</para>
    /// </summary>
    private async Task<IReadOnlyList<FrmRailSegment>?> GetRailsAsync(CancellationToken ct)
    {
        if (_cachedRails is not null && DateTime.UtcNow - _railsFetchedUtc < RailCacheFor)
            return _cachedRails;

        await _cacheGate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: whoever we queued behind has probably
            // just filled it.
            if (_cachedRails is not null && DateTime.UtcNow - _railsFetchedUtc < RailCacheFor)
                return _cachedRails;

            var fetched = await _frm.GetTrainRailsAsync(ct);

            if (fetched is null)
            {
                // Keep whatever we already had. Stale track beats no map, and
                // the geometry barely changes: a server blip should not turn a
                // working map into an error message.
                _logger.LogDebug("getTrainRails was unreachable; keeping the cached geometry");
                return _cachedRails;
            }

            _cachedRails = fetched;
            _railsFetchedUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Cached {Count} Satisfactory rail segment(s) for the map, {Metres:0} m of track",
                fetched.Count, fetched.Sum(SatisfactoryRail.LengthUnits) / UnitsPerMetre);

            return _cachedRails;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    // ─── Drawing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Static on purpose: the whole draw is a pure function of the three lists,
    /// which is what lets a render harness call it with synthetic geometry and
    /// look at the result without a live server to fetch from.
    /// </summary>
    private static byte[] Render(
        IReadOnlyList<IReadOnlyList<FrmLocation>> polylines,
        IReadOnlyList<FrmTrainStation> stations,
        IReadOnlyList<FrmTrain> trains,
        string subtitle)
    {
        var bounds = Bounds(polylines, stations, trains);

        const float plotLeft = MarginLeft;
        const float plotTop = MarginTop;
        const float plotRight = CanvasWidth - MarginRight;
        const float plotWidth = plotRight - plotLeft;

        // Height derived from the content, so the drawing fills the frame at the
        // one scale both axes share. Clamped, so a degenerate aspect (a single
        // dead-straight line) still produces a sane image.
        var plotHeight = Math.Clamp(
            (float)(plotWidth * bounds.Height / bounds.Width), MinPlotHeight, MaxPlotHeight);

        var canvasHeight = (int)Math.Round(plotHeight + MarginTop + MarginBottom);
        var plotBottom = plotTop + plotHeight;

        // ONE scale for both axes, so the network keeps its real shape. Fitting
        // each axis independently would stretch a long thin main line to fill
        // the canvas and make a straight run look like a dogleg. With the
        // derived height the two are usually equal; they differ only when the
        // clamp above bit.
        var scale = Math.Min(plotWidth / bounds.Width, plotHeight / bounds.Height);

        // Centre the leftover space, so a clamped network sits in the middle of
        // the frame rather than pinned to a corner.
        var offsetX = plotLeft + (plotWidth - (float)(bounds.Width * scale)) / 2f;
        var offsetY = plotTop + (plotHeight - (float)(bounds.Height * scale)) / 2f;

        SKPoint ToPixel(FrmLocation p)
        {
            var x = offsetX + (float)((p.X - bounds.MinX) * scale);
            var yFromTop = FlipY
                ? (float)((bounds.MaxY - p.Y) * scale)
                : (float)((p.Y - bounds.MinY) * scale);

            return new SKPoint(x, offsetY + yFromTop);
        }

        var info = new SKImageInfo(CanvasWidth, canvasHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        DrawBackground(canvas, canvasHeight);

        using var titleFont = Font(26, bold: true);
        using var subtitleFont = Font(14);
        using var labelFont = Font(13);
        using var smallBoldFont = Font(12, bold: true);

        DrawRails(canvas, polylines, ToPixel);

        // Markers first, labels last, in one pass over the whole map. Drawing
        // each category's labels next to its own markers is what produced the
        // unreadable smear over the clan's yard: five stations and a train
        // inside 200 pixels, every label centred on its own marker with no idea
        // the others existed.
        var labels = new List<MapLabel>();
        var occupied = new List<SKRect>();

        // The header and the legend own the top strip. Reserving it means a
        // label near the top of the network gets nudged down rather than
        // printed through the title.
        occupied.Add(new SKRect(0, 0, CanvasWidth, MarginTop - 20f));

        DrawStations(canvas, stations, ToPixel, labels, occupied);
        DrawTrains(canvas, trains, ToPixel, labels, occupied);

        var hidden = DrawLabels(canvas, labelFont, labels, occupied, canvasHeight);

        DrawScaleBar(canvas, smallBoldFont, scale, plotLeft, plotBottom, hidden);
        DrawHeader(canvas, titleFont, subtitleFont, subtitle);
        DrawLegend(canvas, labelFont, trains);

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>
    /// Track, drawn twice: a wide dim stroke under a narrow bright one.
    ///
    /// <para>The under-stroke is what stops a dense yard reading as a solid
    /// blob. Parallel lines a few pixels apart merge at this scale, and the
    /// halo gives the eye an edge to separate them by.</para>
    /// </summary>
    private static void DrawRails(
        SKCanvas canvas,
        IReadOnlyList<IReadOnlyList<FrmLocation>> polylines,
        Func<FrmLocation, SKPoint> toPixel)
    {
        using var halo = new SKPaint
        {
            Color = Hex(RailHex, 40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        using var line = new SKPaint
        {
            Color = Hex(RailHex, 205),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        using var path = new SKPath();

        foreach (var points in polylines)
        {
            path.MoveTo(toPixel(points[0]));
            for (var i = 1; i < points.Count; i++) path.LineTo(toPixel(points[i]));
        }

        canvas.DrawPath(path, halo);
        canvas.DrawPath(path, line);
    }

    private static void DrawStations(
        SKCanvas canvas,
        IReadOnlyList<FrmTrainStation> stations,
        Func<FrmLocation, SKPoint> toPixel,
        List<MapLabel> labels,
        List<SKRect> occupied)
    {
        using var fill = new SKPaint { Color = Hex(StationHex, 230), IsAntialias = true };
        using var ring = new SKPaint
        {
            Color = Hex(StationHex, 90),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
        };

        foreach (var station in stations.Where(s => s.Location is not null))
        {
            var p = toPixel(station.Location!);

            canvas.DrawCircle(p, 8f, ring);
            canvas.DrawRect(new SKRect(p.X - 3.5f, p.Y - 3.5f, p.X + 3.5f, p.Y + 3.5f), fill);

            // The marker is an obstacle in its own right. Without this a label
            // pushed clear of other TEXT can still land on top of a neighbouring
            // station's dot, which reads as a mislabelled station rather than as
            // a collision.
            occupied.Add(new SKRect(p.X - 9f, p.Y - 9f, p.X + 9f, p.Y + 9f));

            labels.Add(new MapLabel(p, SatisfactoryRail.DisplayName(station), StationHex, PriorityStation));
        }
    }

    /// <summary>
    /// Trains as a coloured dot with a heading tick.
    ///
    /// <para>The tick is worth the few lines: on a static image a dot says where
    /// a train is and nothing else, and "which way is it going" is most of what
    /// somebody looking at a rail map wants to know.</para>
    /// </summary>
    private static void DrawTrains(
        SKCanvas canvas,
        IReadOnlyList<FrmTrain> trains,
        Func<FrmLocation, SKPoint> toPixel,
        List<MapLabel> labels,
        List<SKRect> occupied)
    {
        foreach (var train in trains.Where(t => t.Location is not null))
        {
            var p = toPixel(train.Location!);

            var hex = train.Derailed ? DerailedHex
                : SatisfactoryRail.IsStationary(train) ? StoppedHex
                : MovingHex;

            using (var glow = new SKPaint { Color = Hex(hex, 55), IsAntialias = true })
                canvas.DrawCircle(p, 11f, glow);

            using (var dot = new SKPaint { Color = Hex(hex, 245), IsAntialias = true })
                canvas.DrawCircle(p, 5.5f, dot);

            // Rotation is degrees clockwise from north, so it converts to screen
            // space as sin for x and negative cos for y.
            if (!SatisfactoryRail.IsStationary(train) && train.Location!.Rotation != 0)
            {
                var radians = train.Location.Rotation * Math.PI / 180.0;
                var tip = new SKPoint(
                    p.X + (float)(Math.Sin(radians) * 15.0),
                    p.Y - (float)(Math.Cos(radians) * 15.0));

                using var tick = new SKPaint
                {
                    Color = Hex(hex, 200),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.4f,
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true,
                };

                canvas.DrawLine(p, tip, tick);
            }

            occupied.Add(new SKRect(p.X - 12f, p.Y - 12f, p.X + 12f, p.Y + 12f));

            // A derailed or stopped train outranks a station name for the last
            // free slot in a crowded yard: the station is on the map every day
            // and the stopped train is the reason somebody opened it.
            var priority = train.Derailed ? PriorityUrgent
                : SatisfactoryRail.IsStationary(train) ? PriorityUrgent
                : PriorityTrain;

            labels.Add(new MapLabel(p, SatisfactoryRail.DisplayName(train), hex, priority));
        }
    }

    // ─── Labels ──────────────────────────────────────────────────────────────

    private const int PriorityUrgent  = 0;
    private const int PriorityStation = 1;
    private const int PriorityTrain   = 2;

    /// <summary>One thing wanting a name printed near it.</summary>
    private sealed record MapLabel(SKPoint Anchor, string Text, string Hex, int Priority);

    /// <summary>
    /// Candidate offsets for a label, in the order they are tried: the anchor
    /// point plus this vector gives the text's left-baseline.
    ///
    /// <para>Ordered by how naturally each reads, not by distance. Directly
    /// above is the convention for a map pin, so it goes first; below is the
    /// obvious second. The side placements come next because horizontal space
    /// is usually what a crowded yard has left. The long offsets at the end get
    /// a leader line, which costs a little clutter and is still far better than
    /// dropping the name.</para>
    /// </summary>
    private static readonly SKPoint[] LabelOffsets =
    {
        new(0, -14), new(0, 22),
        new(13, 5), new(-13, 5),
        new(11, -13), new(-11, -13), new(11, 20), new(-11, 20),
        new(0, -30), new(0, 38), new(26, 5), new(-26, 5),
        new(0, -46), new(0, 54), new(42, 5), new(-42, 5),
        new(0, -62), new(0, 70), new(62, 5), new(-62, 5),
    };

    /// <summary>
    /// Beyond this much offset the label no longer visually belongs to its
    /// marker, so a leader line is drawn to reconnect them.
    /// </summary>
    private const float LeaderThreshold = 26f;

    /// <summary>
    /// A marker with another marker this close is "crowded", and its label gets
    /// a leader line no matter how near it was placed.
    ///
    /// <para>This is the yard case, and proximity alone is the reason. Five
    /// stations eight pixels apart get five labels fanned neatly around them
    /// and the map is still lying by omission: nothing says WHICH dot is
    /// "Steel Dispatch". A short leader off each label gives every name a
    /// distinct angle back to its own marker, which is the only cue that
    /// survives at this zoom.</para>
    /// </summary>
    private const float CrowdRadius = 34f;

    /// <summary>
    /// Bound on how many labels are considered. Placement is O(n²) against the
    /// occupied list, and past this point the map is a wall of text anyway.
    /// </summary>
    private const int MaxLabels = 60;

    /// <summary>
    /// Places every label greedily, highest priority first, in the first
    /// candidate slot that hits nothing already drawn.
    ///
    /// <para>Greedy rather than an optimising pass because the failure mode
    /// matters more than the packing: with priorities, the labels that get
    /// dropped when a yard is genuinely too dense are the least useful ones,
    /// and that is worth more than fitting one extra name.</para>
    ///
    /// <para>Returns how many were dropped, which the caller prints. A map that
    /// silently omits names looks complete and is not, and somebody hunting for
    /// a station that isn't drawn has no way to tell the difference.</para>
    /// </summary>
    private static int DrawLabels(
        SKCanvas canvas,
        SKFont font,
        IReadOnlyList<MapLabel> labels,
        List<SKRect> occupied,
        int canvasHeight)
    {
        var metrics = font.Metrics;
        var ascent = -metrics.Ascent;
        var descent = metrics.Descent;

        // Text over track needs an outline to stay legible. Drawn as a stroke
        // under the fill rather than a drop shadow: a shadow only works against
        // a lighter background, and here the text can land on a pale rail line
        // or on the dark panel with equal likelihood.
        using var halo = new SKPaint
        {
            Color = Hex(BgBottomHex, 215),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.2f,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        // Bright enough to actually trace. The first attempt used alpha 70 and
        // the lines were invisible against the panel at the exact zoom where
        // they were the only thing telling five stacked names apart.
        using var leader = new SKPaint
        {
            Color = Hex(InkHex, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f,
            IsAntialias = true,
        };

        var dropped = 0;

        var ordered = labels
            .OrderBy(l => l.Priority)
            .ThenBy(l => l.Anchor.Y)
            .Take(MaxLabels)
            .ToList();

        dropped += Math.Max(0, labels.Count - ordered.Count);

        // Computed against EVERY anchor, not just the ones that got placed: a
        // marker whose own label was dropped still crowds its neighbours.
        var anchors = labels.Select(l => l.Anchor).ToList();

        bool IsCrowded(SKPoint p) => anchors.Any(other =>
            !(Math.Abs(other.X - p.X) < 0.01f && Math.Abs(other.Y - p.Y) < 0.01f)
            && Math.Abs(other.X - p.X) < CrowdRadius
            && Math.Abs(other.Y - p.Y) < CrowdRadius);

        foreach (var label in ordered)
        {
            var width = font.MeasureText(label.Text);
            SKRect? placed = null;
            SKPoint baseline = default;

            foreach (var offset in LabelOffsets)
            {
                // Offsets are expressed from the label's CENTRE so the vertical
                // candidates stay centred over the marker; the baseline is
                // derived from that.
                var candidateBaseline = new SKPoint(
                    label.Anchor.X + offset.X - width / 2f,
                    label.Anchor.Y + offset.Y);

                // Side placements read better anchored to their inner edge than
                // centred, or the text sits half on top of the marker.
                if (offset.X > 0) candidateBaseline.X = label.Anchor.X + offset.X;
                else if (offset.X < 0) candidateBaseline.X = label.Anchor.X + offset.X - width;

                var rect = new SKRect(
                    candidateBaseline.X - 3f,
                    candidateBaseline.Y - ascent - 2f,
                    candidateBaseline.X + width + 3f,
                    candidateBaseline.Y + descent + 2f);

                // Off-canvas is a rejection like any other. A name clipped by
                // the edge is worse than one nudged to the other side.
                if (rect.Left < 4 || rect.Right > CanvasWidth - 4
                    || rect.Top < 4 || rect.Bottom > canvasHeight - 4) continue;

                if (occupied.Any(o => o.IntersectsWith(rect))) continue;

                placed = rect;
                baseline = candidateBaseline;
                break;
            }

            if (placed is null)
            {
                dropped++;
                continue;
            }

            occupied.Add(placed.Value);

            var pushed = Math.Abs(baseline.Y - label.Anchor.Y) > LeaderThreshold
                         || Math.Abs(baseline.X + width / 2f - label.Anchor.X) > LeaderThreshold;

            if (pushed || IsCrowded(label.Anchor))
            {
                // Meet the label box at the point nearest the anchor, so the
                // line ends ON the text rather than crossing it.
                var edge = new SKPoint(
                    Math.Clamp(label.Anchor.X, placed.Value.Left, placed.Value.Right),
                    Math.Clamp(label.Anchor.Y, placed.Value.Top, placed.Value.Bottom));

                canvas.DrawLine(label.Anchor, edge, leader);
            }

            canvas.DrawText(label.Text, baseline.X, baseline.Y, font, halo);

            // Tinted toward the marker's own colour rather than plain white, so
            // in a cluster the eye can pair a name with its dot even when the
            // leader line is short.
            using var ink = new SKPaint { Color = Hex(label.Hex, 235), IsAntialias = true };
            canvas.DrawText(label.Text, baseline.X, baseline.Y, font, ink);
        }

        return dropped;
    }

    /// <summary>
    /// A scale bar, chosen as a round distance rather than a round pixel width.
    ///
    /// <para>Because the view is fitted to the network, the zoom differs on every
    /// render, and without this there is no way to tell a yard from a
    /// continent-spanning main line: both fill the frame.</para>
    /// </summary>
    private static void DrawScaleBar(
        SKCanvas canvas, SKFont font, double pixelsPerUnit, float left, float bottom, int hiddenLabels)
    {
        var pixelsPerMetre = pixelsPerUnit * UnitsPerMetre;
        if (pixelsPerMetre <= 0 || !double.IsFinite(pixelsPerMetre)) return;

        // Longest round distance that fits in a fifth of the canvas. Anything
        // longer starts competing with the map for space.
        double[] candidates = { 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        var maxPixels = CanvasWidth / 5.0;

        var metres = candidates.LastOrDefault(m => m * pixelsPerMetre <= maxPixels);
        if (metres <= 0) metres = candidates[0];

        var width = (float)(metres * pixelsPerMetre);
        var y = bottom + 30f;

        using var bar = new SKPaint
        {
            Color = Hex(InkHex, 130),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
        };

        canvas.DrawLine(left, y, left + width, y, bar);
        canvas.DrawLine(left, y - 4f, left, y + 4f, bar);
        canvas.DrawLine(left + width, y - 4f, left + width, y + 4f, bar);

        using var text = new SKPaint { Color = Hex(InkHex, 150), IsAntialias = true };
        var caption = metres >= 1000 ? $"{metres / 1000:0.#} km" : $"{metres:0} m";
        canvas.DrawText(caption, left + width + 10f, y + 4f, font, text);

        // Say so when names were dropped. An incomplete map that looks complete
        // sends somebody hunting for a station that was never drawn.
        if (hiddenLabels <= 0) return;

        using var note = new SKPaint { Color = Hex(InkHex, 95), IsAntialias = true };
        canvas.DrawText(
            $"{hiddenLabels} label(s) hidden, too crowded to place",
            left + width + 10f + font.MeasureText(caption) + 22f, y + 4f, font, note);
    }

    private static void DrawHeader(SKCanvas canvas, SKFont titleFont, SKFont subtitleFont, string subtitle)
    {
        using (var paint = new SKPaint { Color = Hex(InkHex, 240), IsAntialias = true })
            canvas.DrawText("Rail network", MarginLeft - 14f, 46f, titleFont, paint);

        if (string.IsNullOrWhiteSpace(subtitle)) return;

        using (var paint = new SKPaint { Color = Hex(InkHex, 110), IsAntialias = true })
            canvas.DrawText(subtitle, MarginLeft - 14f, 70f, subtitleFont, paint);
    }

    /// <summary>
    /// Only the states actually on the map get a legend entry. A permanent
    /// "Derailed" swatch on a healthy network trains people to stop reading it.
    /// </summary>
    private static void DrawLegend(SKCanvas canvas, SKFont font, IReadOnlyList<FrmTrain> trains)
    {
        // The swatch SHAPE matches the marker shape, not just its colour.
        // Stations are squares on the map and trains are dots, and carrying that
        // through means the legend still reads correctly for anyone who can't
        // separate the gold and the orange.
        var entries = new List<(string Hex, string Label, bool Square)>
        {
            (StationHex, "Station", true),
        };

        if (trains.Any(t => !t.Derailed && !SatisfactoryRail.IsStationary(t)))
            entries.Add((MovingHex, "Moving", false));

        if (trains.Any(t => !t.Derailed && SatisfactoryRail.IsStationary(t)))
            entries.Add((StoppedHex, "Stopped", false));

        if (trains.Any(t => t.Derailed))
            entries.Add((DerailedHex, "Derailed", false));

        const float swatch = 12f;
        const float gap = 8f;
        const float itemGap = 22f;
        const float y = 40f;

        var x = (float)(CanvasWidth - MarginRight);

        using var labelPaint = new SKPaint { Color = Hex(InkHex, 200), IsAntialias = true };

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var (hex, text, square) = entries[i];

            x -= font.MeasureText(text);
            canvas.DrawText(text, x, y + 10f, font, labelPaint);

            x -= gap + swatch;

            using (var paint = new SKPaint { Color = Hex(hex, 230), IsAntialias = true })
            {
                if (square)
                    canvas.DrawRect(new SKRect(x + 1f, y + 1f, x + swatch - 1f, y + swatch - 1f), paint);
                else
                    canvas.DrawCircle(x + swatch / 2f, y + swatch / 2f, swatch / 2f, paint);
            }

            x -= itemGap;
        }
    }

    private static void DrawBackground(SKCanvas canvas, int canvasHeight)
    {
        var full = new SKRect(0, 0, CanvasWidth, canvasHeight);

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0), new SKPoint(0, canvasHeight),
                   new[] { Hex(BgTopHex), Hex(BgBottomHex) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(full, paint);
        }

        using (var shader = SKShader.CreateRadialGradient(
                   new SKPoint(CanvasWidth * 0.5f, canvasHeight * 0.45f), CanvasWidth * 0.62f,
                   new[] { Hex(LiftHex, 90), Hex(LiftHex, 0) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(full, paint);
        }
    }

    // ─── Geometry ────────────────────────────────────────────────────────────

    private sealed record ViewBounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => Math.Max(MaxX - MinX, 1);
        public double Height => Math.Max(MaxY - MinY, 1);
    }

    /// <summary>
    /// The extent of everything being drawn, padded a little.
    ///
    /// <para>Padding is proportional rather than a fixed number of world units,
    /// because the same map is used for a four-station starter loop and for a
    /// network spanning the map, and a constant that suits one looks absurd on
    /// the other. A degenerate extent (one station, no track) falls back to a
    /// fixed box so the scale calculation can't divide by zero.</para>
    /// </summary>
    private static ViewBounds Bounds(
        IReadOnlyList<IReadOnlyList<FrmLocation>> polylines,
        IReadOnlyList<FrmTrainStation> stations,
        IReadOnlyList<FrmTrain> trains)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Include(FrmLocation p)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return;

            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        foreach (var line in polylines)
            foreach (var point in line) Include(point);

        foreach (var station in stations)
            if (station.Location is not null) Include(station.Location);

        foreach (var train in trains)
            if (train.Location is not null) Include(train.Location);

        if (minX > maxX || minY > maxY)
        {
            // Nothing had a usable coordinate. 100 m square, centred on the
            // origin, so the render still produces an (empty) picture instead of
            // a divide by zero.
            const double half = 50 * UnitsPerMetre;
            return new ViewBounds(-half, -half, half, half);
        }

        var padX = Math.Max((maxX - minX) * 0.06, 20 * UnitsPerMetre);
        var padY = Math.Max((maxY - minY) * 0.06, 20 * UnitsPerMetre);

        return new ViewBounds(minX - padX, minY - padY, maxX + padX, maxY + padY);
    }

    private static SKFont Font(float size, bool bold = false)
    {
        // Null family name = system default, DejaVu Sans on the bot's image.
        // SKTypeface.Default is a shared singleton and deliberately not disposed;
        // SKFont owns scaling state and IS disposed by callers.
        var typeface = SKTypeface.FromFamilyName(null, bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                       ?? SKTypeface.Default;

        return new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
    }

    private static SKColor Hex(string hex, byte alpha = 255) => SKColor.Parse(hex).WithAlpha(alpha);
}
