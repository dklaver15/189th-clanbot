using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>
/// Why a map render produced no image.
///
/// <para><b>The reason this type exists.</b> Both renderers used to answer
/// <c>byte[]?</c>, so "the game server is down", "nobody has built anything",
/// "the response was too big for the droplet" and "the draw threw" all arrived
/// at the command handler as the same null. The handler could then only write
/// one message covering all of them, which is how <c>/satisfactory-map</c> came
/// to answer an outage with "Nothing to map yet" and a suggestion to go read a
/// log that — at the production Information level — had nothing in it. A map
/// that can't be drawn is normal. A map that won't say why costs somebody an
/// evening.</para>
/// </summary>
public enum MapOutcome
{
    /// <summary>There is a PNG.</summary>
    Drawn,

    /// <summary>Nothing answered. The server is down, or FRM isn't listening on its port.</summary>
    Unreachable,

    /// <summary>The server answered and there is genuinely nothing built to draw.</summary>
    Empty,

    /// <summary>
    /// The server is healthy, but a response blew
    /// <c>BotConfig.FrmMaxResponseMegabytes</c>. The factory outgrew the cap,
    /// which is a config decision rather than a fault, and the message must not
    /// read as an outage.
    /// </summary>
    TooLarge,

    /// <summary>
    /// The server answered and we couldn't parse what it said — almost always an
    /// FRM update changing a response shape.
    ///
    /// <para>Kept separate from <see cref="Unreachable"/> on purpose. Both mean
    /// "no data", but this one means the server is HEALTHY, and reporting it as
    /// an outage sends somebody to restart a machine that is fine while the
    /// actual fix is a model change in this codebase.</para>
    /// </summary>
    Unreadable,

    /// <summary>A focus was given and nothing in the world matched it.</summary>
    NoMatch,

    /// <summary>The draw threw. A code bug; the stack is in the log at Error.</summary>
    Failed,
}

/// <summary>
/// The result of a render: the image, or the reason there isn't one.
///
/// <para><see cref="Oversized"/> lists the endpoints skipped for size, and is
/// populated even when the rest of the map drew FINE. A map that quietly omits
/// every machine looks exactly like a map of a factory with no machines, so the
/// caller has to be able to say so underneath an image that did render.</para>
/// </summary>
public sealed record MapRender(
    byte[]? Png,
    MapOutcome Outcome,
    IReadOnlyList<string> Oversized)
{
    private static readonly string[] None = Array.Empty<string>();

    public static MapRender Ok(byte[] png, IReadOnlyList<string>? oversized = null) =>
        new(png, MapOutcome.Drawn, oversized ?? None);

    public static MapRender No(MapOutcome outcome, IReadOnlyList<string>? oversized = null) =>
        new(null, outcome, oversized ?? None);
}

/// <summary>
/// The shared drawing surface behind every Satisfactory map: the world-to-pixel
/// projection, the label placer, and the chrome (background, header, legend,
/// scale bar).
///
/// ── Why this exists ──
/// Extracted from <see cref="SatisfactoryRailMapRenderer"/> when the factory map
/// arrived. Two maps of the same world drawn at two different scales, with two
/// label placers that disagree about crowding, is a guarantee that a fix to one
/// silently leaves the other broken. The rail map's label collision work was
/// hard-won (five stations inside 200 pixels); the factory map inherits it
/// rather than reinventing a worse version.
///
/// ── Two ways to frame ──
/// <see cref="Fit"/> derives the view from the extent of what is being drawn, so
/// the image always frames the actual network. <see cref="Focus"/> takes a
/// centre and a radius, which is the only way to see individual machines: a
/// constructor is 8 m and the clan's rail network is 1.7 km across, so at fitted
/// zoom a machine is a fraction of a pixel.
///
/// ── Lifetime ──
/// Disposable, and single-use. Create, draw into <see cref="Canvas"/>, call
/// <see cref="Finish"/> once for the PNG bytes.
/// </summary>
public sealed class SatisfactoryMapCanvas : IDisposable
{
    /// <summary>
    /// Unreal world units per metre. The engine's default is 1 unit = 1 cm and
    /// Satisfactory keeps it (a foundation is 8 m and measures 800 units). This
    /// is the only unit conversion in the whole map feature: it affects the
    /// scale bar, focus radii and track lengths, and nothing else.
    /// </summary>
    public const double UnitsPerMetre = 100.0;

    /// <summary>
    /// Whether world Y grows downward on screen. Matches the in-game map. This
    /// is the single knob for the map's orientation; if a render ever comes out
    /// mirrored, change it here and both maps follow.
    /// </summary>
    private const bool FlipY = false;

    // ── Layout ───────────────────────────────────────────────────────────────
    //
    // Width is fixed; height is DERIVED from the content's aspect ratio. A fixed
    // canvas letterboxes badly for a map, because one shared scale on both axes
    // keeps the world's real shape: a long east-west main line on a fixed 4:3
    // canvas is a thin ribbon with 40% of the image empty.
    public const int Width = 1500;

    private const int MarginLeft = 60;
    private const int MarginRight = 60;
    private const int MarginTop = 96;
    private const int MarginBottom = 76;

    private const float MinPlotHeight = 420;
    private const float MaxPlotHeight = 1150;

    // ── Palette ──────────────────────────────────────────────────────────────
    public const string InkHex = "ffffff";
    private const string BgTopHex = "141b28";
    private const string BgBottomHex = "080a10";
    private const string LiftHex = "2a3550";

    // ── Label placement ──────────────────────────────────────────────────────
    public const int PriorityUrgent = 0;
    public const int PriorityHigh = 1;
    public const int PriorityNormal = 2;

    /// <summary>
    /// Candidate offsets for a label, in the order they are tried: the anchor
    /// plus this vector gives the text position.
    ///
    /// <para>Ordered by how naturally each reads, not by distance. Directly
    /// above is the convention for a map pin; below is the obvious second. Side
    /// placements come next because horizontal space is usually what a crowded
    /// area has left. The long offsets at the end get a leader line, which costs
    /// a little clutter and beats dropping the name.</para>
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

    /// <summary>Past this offset a label no longer visually belongs to its marker.</summary>
    private const float LeaderThreshold = 26f;

    /// <summary>
    /// A marker with another marker this close is "crowded", and its label gets
    /// a leader line however near it was placed.
    ///
    /// <para>Five stations eight pixels apart get five labels fanned neatly
    /// around them and the map is still lying by omission: nothing says WHICH
    /// dot is which. A short leader gives every name a distinct angle home.</para>
    /// </summary>
    private const float CrowdRadius = 34f;

    /// <summary>
    /// Bound on labels considered. Placement is O(n²) against the occupied list,
    /// and past this the image is a wall of text regardless.
    /// </summary>
    private const int MaxLabels = 60;

    private readonly ViewBounds _bounds;
    private readonly SKSurface _surface;
    private readonly List<MapLabel> _labels = new();
    private readonly List<SKRect> _occupied = new();
    private readonly float _offsetX;
    private readonly float _offsetY;

    private int _droppedLabels;

    private SatisfactoryMapCanvas(ViewBounds bounds, float plotHeight)
    {
        _bounds = bounds;

        PlotHeight = plotHeight;
        Height = (int)Math.Round(plotHeight + MarginTop + MarginBottom);

        // ONE scale for both axes, so the world keeps its real shape. Fitting
        // each axis independently would stretch a long thin base to fill the
        // canvas and make a straight belt run look like a dogleg.
        Scale = Math.Min(PlotWidth / bounds.Width, plotHeight / bounds.Height);

        // Centre the leftover space, so a clamped view sits in the middle of the
        // frame rather than pinned to a corner.
        _offsetX = PlotLeft + (PlotWidth - (float)(bounds.Width * Scale)) / 2f;
        _offsetY = PlotTop + (plotHeight - (float)(bounds.Height * Scale)) / 2f;

        _surface = SKSurface.Create(new SKImageInfo(Width, Height));

        DrawBackground(_surface.Canvas, Height);

        // The header and legend own the top strip. Reserving it means a label
        // near the top gets nudged down rather than printed through the title.
        _occupied.Add(new SKRect(0, 0, Width, MarginTop - 20f));
    }

    public SKCanvas Canvas => _surface.Canvas;

    public int Height { get; }

    /// <summary>Pixels per world unit. Same on both axes, by construction.</summary>
    public double Scale { get; }

    public const float PlotLeft = MarginLeft;
    public const float PlotTop = MarginTop;
    public const float PlotRight = Width - MarginRight;
    public const float PlotWidth = PlotRight - PlotLeft;

    public float PlotHeight { get; }

    public float PlotBottom => PlotTop + PlotHeight;

    /// <summary>
    /// Frames the view to the extent of everything being drawn, padded a little.
    ///
    /// <para>Nothing hardcodes the game world's extents, so the image frames the
    /// clan's actual base rather than four buildings floating in an ocean of
    /// empty map. It also cannot be silently wrong if the world bounds differ
    /// from what a wiki says.</para>
    ///
    /// <para>Padding is proportional rather than a fixed number of world units,
    /// because the same code frames a starter loop and a map-spanning network,
    /// and a constant that suits one looks absurd on the other.</para>
    /// </summary>
    public static SatisfactoryMapCanvas Fit(IEnumerable<FrmLocation> extent)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var p in extent)
        {
            if (p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;

            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        if (minX > maxX || minY > maxY)
        {
            // Nothing had a usable coordinate. A 100 m box at the origin, so the
            // render still produces an (empty) picture instead of dividing by
            // zero.
            const double half = 50 * UnitsPerMetre;
            return FromBounds(new ViewBounds(-half, -half, half, half));
        }

        var padX = Math.Max((maxX - minX) * 0.06, 20 * UnitsPerMetre);
        var padY = Math.Max((maxY - minY) * 0.06, 20 * UnitsPerMetre);

        return FromBounds(new ViewBounds(minX - padX, minY - padY, maxX + padX, maxY + padY));
    }

    /// <summary>
    /// Frames a fixed square around a point. The only way to see individual
    /// machines: at fitted zoom on a kilometre-scale base, an 8 m building is
    /// well under a pixel.
    /// </summary>
    public static SatisfactoryMapCanvas Focus(FrmLocation centre, double radiusMetres)
    {
        var r = Math.Max(radiusMetres, 10) * UnitsPerMetre;
        return FromBounds(new ViewBounds(centre.X - r, centre.Y - r, centre.X + r, centre.Y + r));
    }

    private static SatisfactoryMapCanvas FromBounds(ViewBounds bounds)
    {
        var plotHeight = Math.Clamp(
            (float)(PlotWidth * bounds.Height / bounds.Width), MinPlotHeight, MaxPlotHeight);

        return new SatisfactoryMapCanvas(bounds, plotHeight);
    }

    public SKPoint ToPixel(FrmLocation p)
    {
        var x = _offsetX + (float)((p.X - _bounds.MinX) * Scale);
        var yFromTop = FlipY
            ? (float)((_bounds.MaxY - p.Y) * Scale)
            : (float)((p.Y - _bounds.MinY) * Scale);

        return new SKPoint(x, _offsetY + yFromTop);
    }

    /// <summary>
    /// Whether a world point is inside the framed view, with a generous margin.
    ///
    /// <para>Callers use this to skip geometry rather than to clip it: Skia
    /// would clip correctly anyway, but a focused view of one factory block
    /// should not walk ten thousand off-screen belt splines to draw nothing.
    /// The margin keeps a line that merely PASSES through the view from being
    /// dropped because both its endpoints are outside.</para>
    /// </summary>
    public bool IsVisible(FrmLocation p, double marginUnits = 0)
    {
        var m = marginUnits;
        return p.X >= _bounds.MinX - m && p.X <= _bounds.MaxX + m
            && p.Y >= _bounds.MinY - m && p.Y <= _bounds.MaxY + m;
    }

    /// <summary>World units currently covered by one pixel. Used to decide detail level.</summary>
    public double UnitsPerPixel => Scale > 0 ? 1 / Scale : double.MaxValue;

    /// <summary>
    /// Reserves a rectangle so labels avoid it. Markers register themselves:
    /// a label clear of all TEXT can still land on a neighbouring marker, which
    /// reads as a mislabelled building rather than as a collision.
    /// </summary>
    public void AddObstacle(SKRect rect) => _occupied.Add(rect);

    public void AddLabel(SKPoint anchor, string text, string hex, int priority)
    {
        if (!string.IsNullOrWhiteSpace(text))
            _labels.Add(new MapLabel(anchor, text.Trim(), hex, priority));
    }

    /// <summary>
    /// Draws the labels and all the chrome, then encodes. Call once.
    /// </summary>
    /// <param name="footnote">
    /// Optional extra line beside the scale bar, for whatever the map had to
    /// leave out.
    /// </param>
    public byte[] Finish(
        string title,
        string subtitle,
        IReadOnlyList<LegendEntry> legend,
        string? footnote = null)
    {
        using var titleFont = Font(26, bold: true);
        using var subtitleFont = Font(14);
        using var labelFont = Font(13);
        using var smallBoldFont = Font(12, bold: true);

        DrawLabels(labelFont);

        var hidden = _droppedLabels > 0
            ? $"{_droppedLabels} label(s) hidden, too crowded to place"
            : null;

        var note = string.Join("   ·   ", new[] { footnote, hidden }.Where(s => !string.IsNullOrWhiteSpace(s)));

        DrawScaleBar(smallBoldFont, note);
        DrawHeader(titleFont, subtitleFont, title, subtitle);
        DrawLegend(labelFont, legend);

        using var image = _surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    public void Dispose() => _surface.Dispose();

    // ─── Labels ──────────────────────────────────────────────────────────────

    private sealed record MapLabel(SKPoint Anchor, string Text, string Hex, int Priority);

    public sealed record LegendEntry(string Hex, string Label, bool Square = false);

    /// <summary>
    /// Places every label greedily, highest priority first, in the first
    /// candidate slot that hits nothing already drawn.
    ///
    /// <para>Greedy rather than an optimising pass because the failure mode
    /// matters more than the packing: with priorities, the labels dropped when
    /// an area is genuinely too dense are the least useful ones, and that beats
    /// fitting one extra name.</para>
    /// </summary>
    private void DrawLabels(SKFont font)
    {
        var metrics = font.Metrics;
        var ascent = -metrics.Ascent;
        var descent = metrics.Descent;

        // Text over belts and track needs an outline to stay legible. A stroke
        // under the fill rather than a drop shadow: a shadow only works against
        // a lighter background, and a label can land on a pale line or the dark
        // panel with equal likelihood.
        using var halo = new SKPaint
        {
            Color = Hex(BgBottomHex, 215),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3.2f,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        // Alpha 120, not 70. The first attempt was invisible against the panel
        // at exactly the zoom where it was the only thing telling stacked names
        // apart.
        using var leader = new SKPaint
        {
            Color = Hex(InkHex, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f,
            IsAntialias = true,
        };

        var ordered = _labels
            .OrderBy(l => l.Priority)
            .ThenBy(l => l.Anchor.Y)
            .Take(MaxLabels)
            .ToList();

        _droppedLabels += Math.Max(0, _labels.Count - ordered.Count);

        // Crowding is computed against EVERY anchor, not just placed ones: a
        // marker whose own label was dropped still crowds its neighbours.
        var anchors = _labels.Select(l => l.Anchor).ToList();

        bool IsCrowded(SKPoint p) => anchors.Any(other =>
            !(Math.Abs(other.X - p.X) < 0.01f && Math.Abs(other.Y - p.Y) < 0.01f)
            && Math.Abs(other.X - p.X) < CrowdRadius
            && Math.Abs(other.Y - p.Y) < CrowdRadius);

        foreach (var label in ordered)
        {
            var width = font.MeasureText(label.Text);
            SKRect? placed = null;
            SKPoint at = default;

            foreach (var offset in LabelOffsets)
            {
                var candidate = new SKPoint(
                    label.Anchor.X + offset.X - width / 2f,
                    label.Anchor.Y + offset.Y);

                // Side placements read better anchored to their inner edge than
                // centred, or the text sits half on top of the marker.
                if (offset.X > 0) candidate.X = label.Anchor.X + offset.X;
                else if (offset.X < 0) candidate.X = label.Anchor.X + offset.X - width;

                var rect = new SKRect(
                    candidate.X - 3f,
                    candidate.Y - ascent - 2f,
                    candidate.X + width + 3f,
                    candidate.Y + descent + 2f);

                // Off-canvas is a rejection like any other. A name clipped by
                // the edge is worse than one nudged to the other side.
                if (rect.Left < 4 || rect.Right > Width - 4
                    || rect.Top < 4 || rect.Bottom > Height - 4) continue;

                if (_occupied.Any(o => o.IntersectsWith(rect))) continue;

                placed = rect;
                at = candidate;
                break;
            }

            if (placed is null)
            {
                _droppedLabels++;
                continue;
            }

            _occupied.Add(placed.Value);

            var pushed = Math.Abs(at.Y - label.Anchor.Y) > LeaderThreshold
                         || Math.Abs(at.X + width / 2f - label.Anchor.X) > LeaderThreshold;

            if (pushed || IsCrowded(label.Anchor))
            {
                // Meet the label box at the point nearest the anchor, so the
                // line ends ON the text rather than crossing it.
                var edge = new SKPoint(
                    Math.Clamp(label.Anchor.X, placed.Value.Left, placed.Value.Right),
                    Math.Clamp(label.Anchor.Y, placed.Value.Top, placed.Value.Bottom));

                Canvas.DrawLine(label.Anchor, edge, leader);
            }

            Canvas.DrawText(label.Text, at.X, at.Y, font, halo);

            // Tinted toward the marker's colour rather than plain white, so in a
            // cluster the eye can pair a name with its marker even when the
            // leader line is short.
            using var ink = new SKPaint { Color = Hex(label.Hex, 235), IsAntialias = true };
            Canvas.DrawText(label.Text, at.X, at.Y, font, ink);
        }
    }

    // ─── Chrome ──────────────────────────────────────────────────────────────

    private static void DrawBackground(SKCanvas canvas, int height)
    {
        var full = new SKRect(0, 0, Width, height);

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0), new SKPoint(0, height),
                   new[] { Hex(BgTopHex), Hex(BgBottomHex) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(full, paint);
        }

        using (var shader = SKShader.CreateRadialGradient(
                   new SKPoint(Width * 0.5f, height * 0.45f), Width * 0.62f,
                   new[] { Hex(LiftHex, 90), Hex(LiftHex, 0) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(full, paint);
        }
    }

    private void DrawHeader(SKFont titleFont, SKFont subtitleFont, string title, string subtitle)
    {
        using (var paint = new SKPaint { Color = Hex(InkHex, 240), IsAntialias = true })
            Canvas.DrawText(title, MarginLeft - 14f, 46f, titleFont, paint);

        if (string.IsNullOrWhiteSpace(subtitle)) return;

        using (var paint = new SKPaint { Color = Hex(InkHex, 110), IsAntialias = true })
            Canvas.DrawText(subtitle, MarginLeft - 14f, 70f, subtitleFont, paint);
    }

    /// <summary>
    /// Swatches laid out right-to-left from the header line. The swatch SHAPE
    /// matches the marker shape, so the legend still reads for anyone who can't
    /// separate two similar hues.
    /// </summary>
    private void DrawLegend(SKFont font, IReadOnlyList<LegendEntry> entries)
    {
        const float swatch = 12f;
        const float gap = 8f;
        const float itemGap = 20f;
        const float y = 40f;

        var x = (float)(Width - MarginRight);

        using var labelPaint = new SKPaint { Color = Hex(InkHex, 200), IsAntialias = true };

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];

            x -= font.MeasureText(entry.Label);
            Canvas.DrawText(entry.Label, x, y + 10f, font, labelPaint);

            x -= gap + swatch;

            using (var paint = new SKPaint { Color = Hex(entry.Hex, 230), IsAntialias = true })
            {
                if (entry.Square)
                    Canvas.DrawRect(new SKRect(x + 1f, y + 1f, x + swatch - 1f, y + swatch - 1f), paint);
                else
                    Canvas.DrawCircle(x + swatch / 2f, y + swatch / 2f, swatch / 2f, paint);
            }

            x -= itemGap;
        }
    }

    /// <summary>
    /// A scale bar chosen as a round DISTANCE rather than a round pixel width.
    ///
    /// <para>Because the view is fitted or focused, the zoom differs on every
    /// render, and without this there is no telling a factory block from a
    /// continent-spanning main line: both fill the frame.</para>
    /// </summary>
    private void DrawScaleBar(SKFont font, string? footnote)
    {
        var pixelsPerMetre = Scale * UnitsPerMetre;
        if (pixelsPerMetre <= 0 || !double.IsFinite(pixelsPerMetre)) return;

        double[] candidates = { 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        var maxPixels = Width / 5.0;

        var metres = candidates.LastOrDefault(m => m * pixelsPerMetre <= maxPixels);
        if (metres <= 0) metres = candidates[0];

        var width = (float)(metres * pixelsPerMetre);
        var y = PlotBottom + 30f;

        using var bar = new SKPaint
        {
            Color = Hex(InkHex, 130),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
        };

        Canvas.DrawLine(PlotLeft, y, PlotLeft + width, y, bar);
        Canvas.DrawLine(PlotLeft, y - 4f, PlotLeft, y + 4f, bar);
        Canvas.DrawLine(PlotLeft + width, y - 4f, PlotLeft + width, y + 4f, bar);

        using var text = new SKPaint { Color = Hex(InkHex, 150), IsAntialias = true };
        var caption = metres >= 1000 ? $"{metres / 1000:0.#} km" : $"{metres:0} m";
        Canvas.DrawText(caption, PlotLeft + width + 10f, y + 4f, font, text);

        if (string.IsNullOrWhiteSpace(footnote)) return;

        using var note = new SKPaint { Color = Hex(InkHex, 95), IsAntialias = true };
        Canvas.DrawText(footnote,
            PlotLeft + width + 10f + font.MeasureText(caption) + 22f, y + 4f, font, note);
    }

    // ─── Primitives ──────────────────────────────────────────────────────────

    public static SKFont Font(float size, bool bold = false)
    {
        // Null family name = system default, DejaVu Sans on the bot's image.
        // SKTypeface.Default is a shared singleton and deliberately not disposed;
        // SKFont owns scaling state and IS disposed by callers.
        var typeface = SKTypeface.FromFamilyName(null, bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                       ?? SKTypeface.Default;

        return new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
    }

    public static SKColor Hex(string hex, byte alpha = 255) => SKColor.Parse(hex).WithAlpha(alpha);

    private sealed record ViewBounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => Math.Max(MaxX - MinX, 1);
        public double Height => Math.Max(MaxY - MinY, 1);
    }
}
