using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>
/// Renders per-member activity charts as PNG byte arrays for inline embed
/// attachment. Returns null on any failure so callers can degrade
/// gracefully — chart rendering is "nice to have," not a hard dependency
/// of the surrounding command.
///
/// ── Why a separate service ──
/// Chart generation is heavy (loads SkiaSharp native lib, allocates a
/// drawing surface, runs a font system). Isolating it behind an injected
/// service lets callers swap in a no-op implementation for tests and
/// keeps the timeline command focused on data aggregation.
///
/// ── Why we draw directly with SkiaSharp ──
/// Earlier iterations of this renderer used ScottPlot. We hit two real
/// problems with that path. First, ScottPlot's 5.0 → 5.1 refactor moved
/// or removed the gradient-hatch API the cookbook documents, so every
/// styling polish-pass required guessing at namespace paths and rebuilding
/// to find out. Second, the rendered output silently went missing in
/// Discord embeds when one of those guesses threw at runtime — the whole
/// chart-or-nothing failure mode of the wrapping try/catch made the
/// renderer fragile. SkiaSharp is what ScottPlot uses under the hood
/// anyway, so calling into it directly trades a few hundred lines of
/// "draw rectangle, draw line, draw text" for total control of every
/// pixel and zero abstraction layer to debug. The native binary footprint
/// is identical (SkiaSharp ships the same .so file either way).
///
/// ── Failure handling ──
/// The whole method is wrapped in try/catch and returns null on
/// failure with an error-level log. Callers MUST check for null and
/// either omit the chart from the response or fall back to text-only.
/// Typical failure modes: SkiaSharp native lib missing (Docker missed
/// SkiaSharp.NativeAssets.Linux.NoDependencies), no fonts available
/// (Docker missed fonts-dejavu-core), or runtime out-of-memory on
/// extreme data sizes.
///
/// ── Discord theming ──
/// All colors are sampled from Discord's dark-theme palette so the
/// generated chart drops cleanly into the embed without looking like a
/// foreign element. The 189th-gold accent picks up the same color the
/// rest of the bot uses on auto-promotion announcements.
///
/// ── Output dimensions ──
/// 1800×600 — wide enough to fit 90 daily bars with breathing room,
/// short enough that the chart doesn't dominate the embed on mobile.
/// Discord renders attached PNGs at native pixel size up to roughly
/// 600px wide on the standard message column, so we generate at 3×
/// width for retina sharpness when the user clicks to expand.
/// </summary>
public class MemberActivityChartRenderer
{
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<MemberActivityChartRenderer> _logger;

    // ── Sci-fi-HUD palette ──────────────────────────────────────────────
    // Hex strings without leading '#' because SKColor.Parse accepts both
    // but the parameterless overload is clearest with bare hex. Verified
    // against Discord's dark-theme palette + 189th brand gold.
    private const string BackgroundHex = "0d1017";   // deep console black (whole canvas)
    private const string AxisColorHex  = "ffffff";   // axis labels & title (white for max legibility)
    private const string GridColorHex  = "ffd86b";   // grid lines (gold-tinted, used at very low alpha)
    private const string BarColorHex   = "5865f2";   // Discord blurple (bars)
    private const string LineColorHex  = "ffd86b";   // brighter "command-deck" gold (was 189th #c9a647)

    // ── Layout (pixels) ─────────────────────────────────────────────────
    // Margins reserve space for title (top), Y-axis labels (left), and
    // X-axis date labels (bottom). Right margin only needs to clear the
    // rightmost bar from the canvas edge.
    private const int CanvasWidth    = 1800;
    private const int CanvasHeight   = 600;
    private const int MarginLeft     = 80;
    private const int MarginRight    = 30;
    private const int MarginTop      = 70;
    private const int MarginBottom   = 55;

    /// <summary>How many days of history the chart spans.</summary>
    private const int WindowDays = 90;

    /// <summary>
    /// Width of the trailing rolling average, in days. 7 is the standard
    /// "weekly engagement trend" window; smoother than daily counts but
    /// still responsive enough to show real changes in engagement.
    /// </summary>
    private const int RollingWindowDays = 7;

    /// <summary>Number of horizontal grid lines (and Y-axis labels).</summary>
    private const int YAxisGridSteps = 5;

    /// <summary>Number of X-axis date labels along the bottom.</summary>
    private const int XAxisTickCount = 7;

    /// <summary>Gap between adjacent bar pixels (visual separation).</summary>
    private const float BarGap = 3f;

    public MemberActivityChartRenderer(
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<MemberActivityChartRenderer> logger)
    {
        _services = services;
        _config   = config.Value;
        _logger   = logger;
    }

    /// <summary>
    /// Renders a 90-day daily-activity chart for the given member.
    /// Bars: messages per UTC day. Line overlay: trailing 7-day rolling
    /// average of the same series.
    /// </summary>
    /// <returns>
    /// PNG bytes on success, or null on failure (caller should omit the
    /// chart from the response). On null return, the failure has already
    /// been logged at error level.
    /// </returns>
    public async Task<byte[]?> TryRenderActivityChartAsync(
        ulong guildId,
        ulong userId,
        string displayName,
        CancellationToken ct = default)
    {
        try
        {
            // ── Pull daily counts ───────────────────────────────────────
            // Bucket in-memory after a single indexed query. For a member
            // with even a few thousand messages over the window, this is
            // sub-millisecond and avoids EF Core's SQLite DATE-function
            // translation quirks.
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var nowUtc = DateTime.UtcNow;
            var windowStartUtc = nowUtc.Date.AddDays(-(WindowDays - 1));

            var timestamps = await db.MessageEvents
                .AsNoTracking()
                .Where(m => m.GuildId == guildId
                         && m.UserId == userId
                         && m.Timestamp >= windowStartUtc)
                .Select(m => m.Timestamp)
                .ToListAsync(ct);

            // Bucket into a dense day-keyed dictionary so missing days
            // become zeros rather than gaps. Otherwise the bar chart would
            // pull adjacent bars together for periods of total inactivity,
            // misrepresenting time spans.
            var dayCounts = new Dictionary<DateTime, int>();
            for (var d = 0; d < WindowDays; d++)
            {
                dayCounts[windowStartUtc.AddDays(d)] = 0;
            }
            foreach (var ts in timestamps)
            {
                var day = ts.Date;
                if (dayCounts.ContainsKey(day))
                    dayCounts[day]++;
            }

            // Skip rendering entirely when the member has zero messages in
            // the window. A flat-zero chart is not informative and the
            // text fields already make the inactivity visible.
            if (timestamps.Count == 0)
            {
                _logger.LogDebug(
                    "Skipping activity chart for user {UserId}: zero messages in last {Days}d.",
                    userId, WindowDays);
                return null;
            }

            // Ordered day → count for both the bars and the rolling avg.
            var orderedDays = dayCounts.Keys.OrderBy(d => d).ToList();
            var counts = orderedDays.Select(d => (double)dayCounts[d]).ToArray();

            // ── Trailing rolling average ────────────────────────────────
            // For day i, average the prior RollingWindowDays values (clamped
            // at the start). "Trailing" (no future peeking) so the line on
            // the rightmost day reflects the actual most-recent trend.
            var rolling = new double[counts.Length];
            for (var i = 0; i < counts.Length; i++)
            {
                var start = Math.Max(0, i - RollingWindowDays + 1);
                var span = i - start + 1;
                var sum = 0.0;
                for (var j = start; j <= i; j++) sum += counts[j];
                rolling[i] = sum / span;
            }

            // Bail if the user cancelled while we were querying — no point
            // burning the render budget for a request the caller has
            // already abandoned.
            ct.ThrowIfCancellationRequested();

            return RenderChart(orderedDays, counts, rolling, displayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to render activity chart for user {UserId} in guild {GuildId}",
                userId, guildId);
            return null;
        }
    }

    /// <summary>
    /// Synchronous SkiaSharp render path. Kept as a separate method so the
    /// async wrapper above stays focused on data-flow and so the rendering
    /// is testable in isolation if we ever need to.
    /// </summary>
    private byte[] RenderChart(
        IReadOnlyList<DateTime> orderedDays,
        IReadOnlyList<double> counts,
        IReadOnlyList<double> rolling,
        string displayName)
    {
        // ── Axis scale ─────────────────────────────────────────────────
        // Max value drives the Y-axis ceiling. Round up to a "nice" value
        // (5, 10, 25, 50, 100...) so gridlines land on whole numbers — a
        // chart with gridlines at 7.4, 14.8, 22.2 is technically correct
        // and visually awful.
        var dataMax = Math.Max(counts.Max(), rolling.Max());
        var yMax = NiceCeiling(dataMax);

        // Plot rectangle (the area where bars and the trend line live).
        // All coordinates outside of this go to title or axis labels.
        const float plotLeft   = MarginLeft;
        const float plotRight  = CanvasWidth - MarginRight;
        const float plotTop    = MarginTop;
        const float plotBottom = CanvasHeight - MarginBottom;
        const float plotWidth  = plotRight - plotLeft;
        const float plotHeight = plotBottom - plotTop;

        // Coordinate transforms. Closures so the loops below stay tidy.
        // X: each day occupies a "slot" of plotWidth/WindowDays; we
        // center the trend-line points (and the bars) in the middle of
        // their slot so the chart reads as "this point represents this
        // day" rather than "this point sits at the edge of this day."
        var slotWidth = plotWidth / WindowDays;
        float XToPixel(int dayIndex) => plotLeft + dayIndex * slotWidth + slotWidth / 2f;
        float YToPixel(double y) => plotBottom - (float)(y / yMax) * plotHeight;

        // ── Surface ────────────────────────────────────────────────────
        // SKSurface is the modern way to allocate a drawable buffer.
        // Snapshot() at the end converts to an SKImage for encoding.
        var info = new SKImageInfo(CanvasWidth, CanvasHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(BackgroundHex));

        // ── Fonts ──────────────────────────────────────────────────────
        // Default font family on Linux Docker resolves to DejaVu Sans
        // (provided by fonts-dejavu-core, which the Dockerfile already
        // installs). Null typeface name = system default; fall back to
        // SKTypeface.Default if the system can't produce one at all so
        // we don't NRE on a misconfigured container.
        //
        // We deliberately don't `using`-wrap the typefaces. Two reasons:
        // (1) SKTypeface is internally reference-counted and the GC
        // handles native cleanup via finalizer; manual disposal isn't
        // required. (2) SKTypeface.Default is a shared singleton, so if
        // FromFamilyName returns null and the fallback kicks in,
        // disposing it would corrupt every future render in the process.
        // SKFont DOES need disposal (it owns scaling state) — so those
        // get `using`.
        var titleTypeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold)
                            ?? SKTypeface.Default;
        var bodyTypeface  = SKTypeface.FromFamilyName(null)
                            ?? SKTypeface.Default;
        using var titleFont = new SKFont(titleTypeface, 22f);
        using var labelFont = new SKFont(bodyTypeface, 14f);

        // SkiaSharp's DrawText takes a BASELINE Y, not a top or center.
        // For vertically centering text on a gridline we need to nudge
        // down by roughly half the cap-height. Computing it from font
        // metrics handles font swaps gracefully — if DejaVu Sans isn't
        // available and we fall through to something else, this still
        // looks centered instead of clipped.
        var labelMetrics = labelFont.Metrics;
        var labelVerticalCenter = -(labelMetrics.Ascent + labelMetrics.Descent) / 2f;

        // ── Y-axis: horizontal gridlines + value labels ────────────────
        // Drawn FIRST so bars and the trend line overlay them. The grid
        // is faint enough (~8% alpha) to read as a HUD reticle rather
        // than a structural element competing with the data.
        using (var gridPaint = new SKPaint
        {
            Color = SKColor.Parse(GridColorHex).WithAlpha(20),
            StrokeWidth = 1,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
        })
        using (var axisLabelPaint = new SKPaint
        {
            Color = SKColor.Parse(AxisColorHex),
            IsAntialias = true,
        })
        {
            for (var g = 0; g <= YAxisGridSteps; g++)
            {
                var yValue = yMax * g / YAxisGridSteps;
                var yPixel = YToPixel(yValue);

                canvas.DrawLine(plotLeft, yPixel, plotRight, yPixel, gridPaint);

                // Right-align label against the left margin with 10px
                // breathing room from the gridline start.
                var label = FormatTickLabel(yValue);
                var w = labelFont.MeasureText(label);
                canvas.DrawText(
                    label,
                    plotLeft - 10f - w,
                    yPixel + labelVerticalCenter,
                    labelFont,
                    axisLabelPaint);
            }
        }

        // ── Bars with per-bar vertical gradient ────────────────────────
        // Each bar is a single rectangle painted with a linear gradient
        // shader. The shader's endpoints are the bar's own top and bottom,
        // so a 1-message bar gets the same proportional fade as a
        // 50-message bar — short bars don't end up looking flat-saturated.
        // Saturated blurple at the base; faded to ~10% alpha at the crown.
        var barBottomColor = SKColor.Parse(BarColorHex).WithAlpha(242);   // ~95%
        var barTopColor    = SKColor.Parse(BarColorHex).WithAlpha(26);    // ~10%
        var barWidth = slotWidth - BarGap;

        for (var i = 0; i < counts.Count; i++)
        {
            var value = counts[i];
            if (value <= 0) continue;  // no bar to draw for zero-activity days

            var top    = YToPixel(value);
            var bottom = plotBottom;
            var x0     = plotLeft + i * slotWidth + BarGap / 2f;
            var rect   = new SKRect(x0, top, x0 + barWidth, bottom);

            // Allocate shader and paint inside the loop so each bar gets
            // its own (proportional) gradient. 90 short-lived allocations
            // per render is well below the noise floor.
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, bottom),
                new SKPoint(0, top),
                new[] { barBottomColor, barTopColor },
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(rect, paint);
        }

        // ── Trend-line path (smoothed via mid-point quadratic curves) ──
        // For each interior point P[i], we anchor a quadratic Bézier whose
        // control is P[i] and whose endpoint is the midpoint between P[i]
        // and P[i+1]. Result: a smooth curve that hugs the data but
        // damps the day-to-day jitter that a polyline would expose. This
        // is the same shape ScottPlot's QuadHalfPoint PathStrategy
        // produces — we're recreating that look one method-call deeper.
        using var trendPath = BuildSmoothPath(rolling, XToPixel, YToPixel);

        // Pass 1: glow underlayer. A very wide, very transparent stroke
        // that reads as the trend line's radiated halo rather than as a
        // separate line. Round caps and joins prevent the wide stroke
        // from showing flat ends or mitered angles.
        using (var glowPaint = new SKPaint
        {
            Color = SKColor.Parse(LineColorHex).WithAlpha(50),  // ~20%
            StrokeWidth = 11f,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(trendPath, glowPaint);
        }

        // Pass 2: area fill below the trend line. Vertical gradient gold
        // (saturated near the baseline, fully transparent at the line's
        // height) — this echoes the bars' gradient orientation so the
        // chart reads as one coherent visual language.
        using (var fillPath = BuildAreaFillPath(rolling, XToPixel, YToPixel, plotBottom))
        using (var fillShader = SKShader.CreateLinearGradient(
            new SKPoint(0, plotBottom),
            new SKPoint(0, plotTop),
            new[]
            {
                SKColor.Parse(LineColorHex).WithAlpha(60),
                SKColor.Parse(LineColorHex).WithAlpha(0),
            },
            SKShaderTileMode.Clamp))
        using (var fillPaint = new SKPaint
        {
            Shader = fillShader,
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(fillPath, fillPaint);
        }

        // Pass 3: the sharp trend line itself, sitting on top of its own
        // glow and area-fill. Round joins so the smoothed curve doesn't
        // expose any visible vertex bumps where the quad segments meet.
        using (var linePaint = new SKPaint
        {
            Color = SKColor.Parse(LineColorHex),
            StrokeWidth = 3f,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(trendPath, linePaint);
        }

        // ── "Today" marker ─────────────────────────────────────────────
        // Subtle dashed vertical line at the rightmost day. Pins the eye
        // to "where are we right now" along the 90-day window — a viewer
        // glancing at the chart needs to know whether the visible trend
        // is ancient history or this week's reality. Drawn LAST so it
        // sits above bars and trend; muted alpha keeps it from competing
        // with the data series.
        using (var dashEffect = SKPathEffect.CreateDash(new[] { 6f, 5f }, 0f))
        using (var todayPaint = new SKPaint
        {
            Color = SKColor.Parse(LineColorHex).WithAlpha(140),  // ~55%
            StrokeWidth = 1.5f,
            Style = SKPaintStyle.Stroke,
            PathEffect = dashEffect,
            IsAntialias = true,
        })
        {
            var todayX = XToPixel(rolling.Count - 1);
            canvas.DrawLine(todayX, plotTop, todayX, plotBottom, todayPaint);
        }

        // ── X-axis date labels ─────────────────────────────────────────
        // Evenly spaced ticks across the window. Endpoints are anchored to
        // the first and last days so the viewer always sees the actual
        // window bounds; the interior labels are at proportional positions.
        // Centered under their day-position in the plot.
        using (var dateLabelPaint = new SKPaint
        {
            Color = SKColor.Parse(AxisColorHex),
            IsAntialias = true,
        })
        {
            var labelBaselineY = plotBottom + 22f;
            for (var t = 0; t < XAxisTickCount; t++)
            {
                var dayIndex = (int)Math.Round(
                    (double)t / (XAxisTickCount - 1) * (WindowDays - 1));
                var date = orderedDays[dayIndex];
                var label = date.ToString("MMM d");
                var x = XToPixel(dayIndex);
                var w = labelFont.MeasureText(label);
                canvas.DrawText(
                    label,
                    x - w / 2f,
                    labelBaselineY,
                    labelFont,
                    dateLabelPaint);
            }
        }

        // ── Title (top center) ─────────────────────────────────────────
        // The chart's single source of "what am I looking at" — no
        // separate axis labels or legend, because the gold trend line and
        // blurple bars in a chart titled "Daily activity" don't need
        // further explanation.
        using (var titlePaint = new SKPaint
        {
            Color = SKColor.Parse(AxisColorHex),
            IsAntialias = true,
        })
        {
            var title = $"Daily activity — {displayName} (last {WindowDays} days)";
            var titleWidth = titleFont.MeasureText(title);
            canvas.DrawText(
                title,
                (CanvasWidth - titleWidth) / 2f,
                42f,
                titleFont,
                titlePaint);
        }

        // ── Encode ─────────────────────────────────────────────────────
        // Snapshot the surface to an SKImage, then encode as PNG. PNG
        // quality 100 = lossless (PNG quality is "compression effort,"
        // not lossy compression — 100 just means "spend more CPU for
        // smaller file"). Output is in the low-tens-of-KB range for a
        // 1800×600 chart with mostly flat colors, well within Discord's
        // attachment limit.
        using var image = surface.Snapshot();
        using var pngData = image.Encode(SKEncodedImageFormat.Png, 100);
        return pngData.ToArray();
    }

    /// <summary>
    /// Traces a smooth quadratic-Bézier curve through the given Y values
    /// onto an existing path, assuming the path's current point is
    /// already at the first point. Each interior point becomes a
    /// quadratic control with its endpoint at the midpoint to the next
    /// point — the curve hugs the data while damping daily jitter. This
    /// is the same shape ScottPlot's QuadHalfPoint PathStrategy produces.
    /// </summary>
    private static void TraceSmoothCurve(
        SKPath path,
        IReadOnlyList<double> values,
        Func<int, float> xToPixel,
        Func<double, float> yToPixel)
    {
        if (values.Count <= 1) return;
        if (values.Count == 2)
        {
            path.LineTo(xToPixel(1), yToPixel(values[1]));
            return;
        }

        // Interior points: quadratic Bézier with point i as control and
        // the midpoint between i and i+1 as the segment endpoint.
        for (var i = 1; i < values.Count - 1; i++)
        {
            var cx   = xToPixel(i);
            var cy   = yToPixel(values[i]);
            var midX = (xToPixel(i) + xToPixel(i + 1)) / 2f;
            var midY = (yToPixel(values[i]) + yToPixel(values[i + 1])) / 2f;
            path.QuadTo(cx, cy, midX, midY);
        }
        // Final segment lands exactly on the last data point so the
        // "today" marker aligns with the trend line endpoint.
        path.LineTo(xToPixel(values.Count - 1), yToPixel(values[^1]));
    }

    /// <summary>
    /// Builds the smoothed stroke path for the trend line.
    /// </summary>
    private static SKPath BuildSmoothPath(
        IReadOnlyList<double> values,
        Func<int, float> xToPixel,
        Func<double, float> yToPixel)
    {
        var path = new SKPath();
        if (values.Count == 0) return path;

        path.MoveTo(xToPixel(0), yToPixel(values[0]));
        TraceSmoothCurve(path, values, xToPixel, yToPixel);
        return path;
    }

    /// <summary>
    /// Builds a closed path that follows the smoothed trend curve, then
    /// drops straight down to the baseline and back across to the start
    /// — the shape we fill to render the area under the trend line. We
    /// retrace the curve here rather than reusing the stroke path because
    /// closing/extending the stroke path could distort it for the stroke
    /// pass (and the SKPath state after a copy isn't trivially reliable).
    /// </summary>
    private static SKPath BuildAreaFillPath(
        IReadOnlyList<double> values,
        Func<int, float> xToPixel,
        Func<double, float> yToPixel,
        float baselineY)
    {
        var fill = new SKPath();
        if (values.Count == 0) return fill;

        fill.MoveTo(xToPixel(0), yToPixel(values[0]));
        TraceSmoothCurve(fill, values, xToPixel, yToPixel);

        // Drop to baseline at the right edge, cross back to the left
        // edge along the baseline, then close (which connects baseline-
        // left back up to the curve's starting point).
        fill.LineTo(xToPixel(values.Count - 1), baselineY);
        fill.LineTo(xToPixel(0), baselineY);
        fill.Close();
        return fill;
    }

    /// <summary>
    /// Rounds a value up to a "nice" axis ceiling so gridlines land on
    /// human-readable numbers (5, 10, 25, 50, 100, 200, etc.) instead
    /// of awkward divisions like 7.4 or 31.6.
    /// </summary>
    private static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        if (value <= 5) return 5;

        // Decompose into mantissa + power of 10, snap the mantissa to
        // the next nice value, recombine.
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;  // 1.0 .. 9.999

        double nice;
        if      (normalized <= 1)   nice = 1;
        else if (normalized <= 2)   nice = 2;
        else if (normalized <= 2.5) nice = 2.5;
        else if (normalized <= 5)   nice = 5;
        else                        nice = 10;

        return nice * magnitude;
    }

    /// <summary>
    /// Formats a Y-axis tick value. Integers stay integers; fractional
    /// values (which only arise from the rolling average exceeding the
    /// bar max, which is rare in practice) get a single decimal place.
    /// </summary>
    private static string FormatTickLabel(double value)
    {
        return value == Math.Floor(value)
            ? ((int)value).ToString()
            : value.ToString("F1");
    }
}