using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>
/// Renders Satisfactory charts as PNG byte arrays for embed attachment.
///
/// Two charts:
/// <list type="bullet">
/// <item><b>Power</b> — draw against capacity over time, with fuse trips and
/// near-misses marked. Answers "was that blown fuse coming?", which a live
/// reading can't.</item>
/// <item><b>Playtime</b> — hours per player per day, stacked.</item>
/// </list>
///
/// ── Why SkiaSharp directly, not ScottPlot ──
/// Following <see cref="MemberActivityChartRenderer"/>, which documents the
/// reasoning: ScottPlot's 5.0 → 5.1 refactor moved the styling APIs its own
/// cookbook documents, and a guess that threw at runtime produced a silently
/// missing chart rather than a build error. SkiaSharp is what ScottPlot draws
/// with anyway.
///
/// ── The design was prototyped, not guessed ──
/// Every visual decision below was rendered and looked at before being written
/// here, and several first attempts were thrown away for reasons worth keeping:
/// a shaded "danger zone" under the capacity line read as a second data series
/// and, because capacity changes, rendered as a solid red slab; the battery
/// series on the megawatt axis drew 100% charge at 320 MW, towering over a 60 MW
/// factory. Both are replaced below, and the comments say what they're avoiding.
///
/// ── Failure handling ──
/// Every public method returns null on failure, already logged. Callers MUST
/// treat null as "no chart this time" and fall back to text — a chart is never
/// worth losing the message it was attached to.
/// </summary>
public sealed class SatisfactoryChartRenderer
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SatisfactoryChartRenderer> _logger;

    // ── Palette ──────────────────────────────────────────────────────────────
    private const string BgTopHex     = "141b28";   // background gradient, top
    private const string BgBottomHex  = "080a10";   // background gradient, bottom
    private const string LiftHex      = "2a3550";   // radial lift behind the plot
    private const string PanelHex     = "ffffff";   // plot panel wash, very low alpha
    private const string AxisHex      = "ffffff";
    private const string GridHex      = "8fa3c8";
    private const string DrawLoHex    = "5865f2";   // blurple, at the baseline
    private const string DrawHiHex    = "00d4ff";   // cyan, at the crown
    private const string CapacityHex  = "ffd86b";   // command-deck gold
    private const string CapacityLoHex = "d9ae3a";  // legend swatch, low end
    private const string DangerHex    = "ed4245";
    private const string DangerLoHex  = "a02529";
    private const string BatteryHex   = "3ba55d";
    private const string BatteryHiHex = "7be39b";

    /// <summary>
    /// Per-player bar gradients, low (base) and high (crown). A fixed cycle
    /// rather than a hash of the name: with four players a hash would
    /// occasionally hand two of them near-identical colours and there'd be no
    /// way to override it.
    /// </summary>
    private static readonly string[] PlayerLoHex =
        { "3c46c8", "d9ae3a", "2a8248", "c22f7e", "0086bc", "c55a46" };

    private static readonly string[] PlayerHiHex =
        { "8b94ff", "ffe9a3", "63d98c", "ff8ac7", "5fd8ff", "ffa98f" };

    /// <summary>
    /// Usable palette length. Taken from BOTH arrays so adding a colour to one
    /// and forgetting the other degrades to a shorter palette instead of an
    /// index-out-of-range at render time — the bars index these without a
    /// modulo, relying on Collapse() to bound the count.
    /// </summary>
    private static readonly int PaletteSize = Math.Min(PlayerLoHex.Length, PlayerHiHex.Length);

    // ── Layout ───────────────────────────────────────────────────────────────
    private const int CanvasWidth  = 1800;
    private const int CanvasHeight = 660;
    private const int MarginLeft   = 96;
    private const int MarginRight  = 56;
    private const int MarginTop    = 96;
    private const int MarginBottom = 116;

    private const int XAxisTickCount = 7;

    /// <summary>Load above this share of capacity counts as a near miss.</summary>
    private const double DangerFraction = 0.9;

    /// <summary>
    /// A sampling gap longer than this multiple of the typical interval breaks
    /// the line instead of being interpolated across.
    /// </summary>
    private const int GapMultiple = 3;

    public SatisfactoryChartRenderer(
        IServiceProvider services,
        ILogger<SatisfactoryChartRenderer> logger)
    {
        _services = services;
        _logger = logger;
    }

    // ─── Power ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Grid load over the given window.
    /// </summary>
    /// <param name="subtitle">Second header line, e.g. "Friday, July 24".</param>
    /// <returns>PNG bytes, or null if there's nothing worth drawing.</returns>
    public async Task<byte[]?> TryRenderPowerChartAsync(
        TimeSpan window, string subtitle, CancellationToken ct = default)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var since = DateTime.UtcNow - window;

            // Scope to the CURRENT world. Without this a save wipe inside the
            // retention window splices two unrelated factories into one line,
            // and the discontinuity reads as a real capacity collapse rather
            // than a new save. Seed 0 means "couldn't determine" — those rows
            // are still the current world as far as we know, so they're kept.
            var currentSeed = await db.SatisfactoryMetricSamples
                .AsNoTracking()
                .OrderByDescending(s => s.SampledUtc)
                .Select(s => s.Seed)
                .FirstOrDefaultAsync(ct);

            var samples = await db.SatisfactoryMetricSamples
                .AsNoTracking()
                .Where(s => s.SampledUtc >= since && (s.Seed == currentSeed || s.Seed == 0))
                .OrderBy(s => s.SampledUtc)
                .ToListAsync(ct);

            // Two points is a line segment, not a chart. Below that the axis
            // scaling is meaningless and the result misleads more than it says.
            if (samples.Count < 3)
            {
                _logger.LogDebug(
                    "Skipping Satisfactory power chart: only {Count} sample(s) in the last {Hours}h",
                    samples.Count, (int)window.TotalHours);
                return null;
            }

            ct.ThrowIfCancellationRequested();

            return RenderPowerChart(samples, subtitle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render the Satisfactory power chart");
            return null;
        }
    }

    private byte[] RenderPowerChart(IReadOnlyList<SatisfactoryMetricSample> samples, string subtitle)
    {
        var consumed = samples.Select(s => s.PowerConsumedMw).ToArray();
        var capacity = samples.Select(s => s.PowerCapacityMw).ToArray();

        // 6% headroom so the capacity ceiling never sits flush against the top
        // gridline, where it reads as a chart edge rather than a value.
        var (yMax, yStep) = NiceAxis(Math.Max(consumed.Max(), capacity.Max()) * 1.06);

        var hasBatteries = samples.Any(s => s.BatteryPercent is not null);

        const float plotLeft = MarginLeft;
        const float plotRight = CanvasWidth - MarginRight;
        const float plotTop = MarginTop;
        // Give back the strip's height only when a strip is actually drawn.
        float plotBottom = CanvasHeight - MarginBottom - (hasBatteries ? 26f : 0f);
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;

        // Plotted against TIME, not against sample index. The poll can miss
        // ticks (FRM down, bot restarting), and index-spacing would compress a
        // six-hour outage to the width of a normal interval.
        var t0 = samples[0].SampledUtc;
        var span = samples[^1].SampledUtc - t0;
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMinutes(1);

        float XToPixel(DateTime t) =>
            plotLeft + (float)((t - t0).TotalSeconds / span.TotalSeconds) * plotWidth;
        float YToPixel(double y) => plotBottom - (float)(y / yMax) * plotHeight;

        var info = new SKImageInfo(CanvasWidth, CanvasHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        DrawBackground(canvas);
        DrawPanel(canvas, plotLeft, plotTop, plotRight, plotBottom);

        using var titleFont = Font(26, bold: true);
        using var subtitleFont = Font(14);
        using var labelFont = Font(14);
        using var smallFont = Font(12);
        using var smallBoldFont = Font(12, bold: true);
        using var pillFont = Font(15, bold: true);

        DrawYAxis(canvas, labelFont, yMax, yStep, plotLeft, plotRight, YToPixel,
            v => $"{v:0.###} MW");

        var gap = MedianInterval(samples) * GapMultiple;
        var runs = Runs(samples, gap);

        // ── Near misses ──
        // Not a permanent shaded "danger zone" under the capacity line: that
        // looked like a second data series, sat there all day whether or not
        // anything was wrong, and — because capacity changes — needed a gradient
        // spanning both levels, which rendered the higher one as a solid red
        // slab. Marking the moments load actually got close says the same thing
        // only when it's true.
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].TrippedCount > 0) continue;
            if (capacity[i] <= 0 || consumed[i] < capacity[i] * DangerFraction) continue;

            var x = XToPixel(samples[i].SampledUtc);
            var top = YToPixel(capacity[i]);
            var bottom = YToPixel(consumed[i]);

            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, bottom), new SKPoint(0, top),
                new[] { Hex(DangerHex, 130), Hex(DangerHex, 20) },
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRect(new SKRect(x - 1.5f, top, x + 1.5f, bottom), paint);
        }

        // ── Fuse trips ──
        // Full-height bands, behind the series. A marker on the data point would
        // sit at the bottom of the chart — a trip takes draw to zero — which is
        // exactly where it's least visible.
        foreach (var s in samples.Where(s => s.TrippedCount > 0))
        {
            var x = XToPixel(s.SampledUtc);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, plotBottom), new SKPoint(0, plotTop),
                new[] { Hex(DangerHex, 150), Hex(DangerHex, 10) },
                SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            canvas.DrawRect(new SKRect(x - 2.5f, plotTop, x + 2.5f, plotBottom), paint);
        }

        // ── Consumption ──
        // The gradient is anchored to the tallest READING, not to the top of the
        // canvas. Anchored to the canvas, a factory drawing 60 MW on a 320 MW
        // axis never reaches the hot end of the ramp and the fill is a flat navy
        // wash — a gradient only pays off if the data spans it.
        var drawPeakY = YToPixel(consumed.Max());

        using (var areaPath = SmoothPath(samples, consumed, runs, XToPixel, YToPixel, plotBottom))
        using (var areaShader = SKShader.CreateLinearGradient(
                   new SKPoint(0, plotBottom), new SKPoint(0, drawPeakY),
                   new[] { Hex(DrawLoHex, 20), Hex(DrawLoHex, 130), Hex(DrawHiHex, 105) },
                   new[] { 0f, 0.55f, 1f },
                   SKShaderTileMode.Clamp))
        using (var areaPaint = new SKPaint { Shader = areaShader, IsAntialias = true })
        {
            canvas.DrawPath(areaPath, areaPaint);
        }

        using (var line = SmoothPath(samples, consumed, runs, XToPixel, YToPixel, null))
        {
            using (var outer = Glow(DrawHiHex, 60, 14f, 7f)) canvas.DrawPath(line, outer);
            using (var inner = Glow(DrawLoHex, 90, 7f, 3f)) canvas.DrawPath(line, inner);

            using var stroke = SKShader.CreateLinearGradient(
                new SKPoint(0, plotBottom), new SKPoint(0, drawPeakY),
                new[] { Hex(DrawLoHex), Hex(DrawHiHex) },
                SKShaderTileMode.Clamp);
            using var linePaint = new SKPaint
            {
                Shader = stroke,
                StrokeWidth = 3.2f,
                Style = SKPaintStyle.Stroke,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsAntialias = true,
            };
            canvas.DrawPath(line, linePaint);
        }

        // ── Capacity ceiling ──
        // Dashed with a soft glow, so it reads as a limit rather than another
        // measurement.
        using (var cap = SmoothPath(samples, capacity, runs, XToPixel, YToPixel, null))
        {
            using (var capGlow = Glow(CapacityHex, 45, 9f, 4f)) canvas.DrawPath(cap, capGlow);

            using var dash = SKPathEffect.CreateDash(new[] { 11f, 7f }, 0f);
            using var capPaint = new SKPaint
            {
                Color = Hex(CapacityHex, 215),
                StrokeWidth = 2.2f,
                Style = SKPaintStyle.Stroke,
                PathEffect = dash,
                IsAntialias = true,
            };
            canvas.DrawPath(cap, capPaint);
        }

        // ── Peak marker ──
        var peakIndex = 0;
        for (var i = 1; i < consumed.Length; i++)
            if (consumed[i] > consumed[peakIndex]) peakIndex = i;

        var peakX = XToPixel(samples[peakIndex].SampledUtc);
        var peakY = YToPixel(consumed[peakIndex]);

        using (var halo = new SKPaint
        {
            Color = Hex(DrawHiHex, 55),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f),
        })
        {
            canvas.DrawCircle(peakX, peakY, 9f, halo);
        }

        using (var dot = new SKPaint { Color = Hex(DrawHiHex), IsAntialias = true })
        {
            canvas.DrawCircle(peakX, peakY, 4.2f, dot);
        }

        var peakLabel = $"peak {consumed[peakIndex]:0} MW";

        // Sit the label BELOW the point when it would collide with the capacity
        // ceiling — which is exactly where a peak worth marking tends to be.
        var capAtPeakY = YToPixel(capacity[peakIndex]);
        var peakLabelY = Math.Abs(peakY - 16f - capAtPeakY) > 16f ? peakY - 16f : peakY + 26f;

        using (var peakPaint = new SKPaint { Color = Hex(DrawHiHex, 220), IsAntialias = true })
        {
            canvas.DrawText(peakLabel,
                peakX - smallBoldFont.MeasureText(peakLabel) / 2f, peakLabelY,
                smallBoldFont, peakPaint);
        }

        // ── Latest value ──
        // A history chart makes "what is it right now" harder to read, not
        // easier. The pill puts that back.
        var latest = consumed[^1];
        var capNow = capacity[^1];
        var lastX = XToPixel(samples[^1].SampledUtc);
        var lastY = YToPixel(latest);

        using (var dot = new SKPaint { Color = Hex(DrawHiHex), IsAntialias = true })
        {
            canvas.DrawCircle(lastX, lastY, 5f, dot);
        }

        var pillText = capNow > 0
            ? $"{latest:0} MW  ·  {100 * latest / capNow:0}%"
            : $"{latest:0} MW";

        var pillWidth = pillFont.MeasureText(pillText);
        var pillLeft = lastX - pillWidth - 34f;
        var pillTop = lastY - 30f;
        var pill = new SKRect(pillLeft, pillTop, pillLeft + pillWidth + 22f, pillTop + 28f);

        using (var fill = new SKPaint { Color = Hex(BgBottomHex, 220), IsAntialias = true })
        {
            canvas.DrawRoundRect(pill, 14f, 14f, fill);
        }

        using (var border = new SKPaint
        {
            Color = Hex(DrawHiHex, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        })
        {
            canvas.DrawRoundRect(pill, 14f, 14f, border);
        }

        using (var text = new SKPaint { Color = Hex(AxisHex, 235), IsAntialias = true })
        {
            canvas.DrawText(pillText, pillLeft + 11f, pillTop + 19f, pillFont, text);
        }

        // ── Time axis ──
        var format = span <= TimeSpan.FromHours(36) ? "HH:mm" : "MMM d";
        var ticks = Math.Min(XAxisTickCount, samples.Count);

        using (var axisPaint = new SKPaint { Color = Hex(AxisHex, 150), IsAntialias = true })
        {
            if (ticks >= 2)
            {
                for (var t = 0; t < ticks; t++)
                {
                    var index = (int)Math.Round((double)t / (ticks - 1) * (samples.Count - 1));
                    var time = samples[index].SampledUtc;
                    var text = time.ToString(format);
                    canvas.DrawText(text,
                        XToPixel(time) - labelFont.MeasureText(text) / 2f, plotBottom + 30f,
                        labelFont, axisPaint);
                }
            }
        }

        if (format == "HH:mm")
        {
            using var notePaint = new SKPaint { Color = Hex(AxisHex, 90), IsAntialias = true };
            canvas.DrawText("times UTC", plotLeft, plotBottom + 52f, smallFont, notePaint);
        }

        // ── Battery ──
        // Its OWN track, not the main axis. Overlaying it (charge% scaled to
        // yMax) drew 100% charge at 320 MW — a green mountain range towering
        // over a 60 MW factory, which reads as a power series and not a
        // percentage. A dedicated 0–100% strip is unambiguous and costs 26px.
        if (hasBatteries)
        {
            var stripTop = plotBottom + 58f;
            const float stripHeight = 26f;
            var stripBottom = stripTop + stripHeight;
            var strip = new SKRect(plotLeft, stripTop, plotRight, stripBottom);

            using (var bed = new SKPaint { Color = Hex(PanelHex, 10), IsAntialias = true })
            {
                canvas.DrawRoundRect(strip, 6f, 6f, bed);
            }

            canvas.Save();
            using (var clip = new SKRoundRect(strip, 6f, 6f))
            {
                canvas.ClipRoundRect(clip, SKClipOperation.Intersect, antialias: true);
            }

            using (var battery = new SKPath())
            {
                float BatteryY(double pct) => stripBottom - (float)(pct / 100.0) * stripHeight;

                foreach (var (start, end) in runs)
                {
                    if (end <= start) continue;

                    battery.MoveTo(XToPixel(samples[start].SampledUtc),
                        BatteryY(samples[start].BatteryPercent ?? 0));

                    for (var i = start + 1; i <= end; i++)
                        battery.LineTo(XToPixel(samples[i].SampledUtc),
                            BatteryY(samples[i].BatteryPercent ?? 0));

                    battery.LineTo(XToPixel(samples[end].SampledUtc), stripBottom);
                    battery.LineTo(XToPixel(samples[start].SampledUtc), stripBottom);
                    battery.Close();
                }

                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, stripBottom), new SKPoint(0, stripTop),
                    new[] { Hex(BatteryHex, 200), Hex(BatteryHiHex, 150) },
                    SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader, IsAntialias = true };
                canvas.DrawPath(battery, paint);
            }

            canvas.Restore();

            using var batteryLabel = new SKPaint { Color = Hex(BatteryHex, 190), IsAntialias = true };
            const string caption = "BATTERY";
            canvas.DrawText(caption,
                plotLeft - 14f - smallBoldFont.MeasureText(caption), stripTop + 17f,
                smallBoldFont, batteryLabel);

            var pct = $"{samples[^1].BatteryPercent ?? 0:0}%";
            canvas.DrawText(pct, plotRight + 8f, stripTop + 17f, smallBoldFont, batteryLabel);
        }

        DrawHeader(canvas, titleFont, subtitleFont, "Power", subtitle);
        DrawLegend(canvas, labelFont, new (string, string, string)[]
        {
            (DrawLoHex, DrawHiHex, "Draw"),
            (CapacityLoHex, CapacityHex, "Capacity"),
            (DangerLoHex, DangerHex, "Fuse tripped"),
        });

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    // ─── Playtime ────────────────────────────────────────────────────────────

    /// <summary>
    /// Hours per player per day, stacked, over the given number of local days
    /// ending today.
    /// </summary>
    /// <param name="zone">
    /// Days are bucketed in this timezone, not UTC — a session at 9pm Central
    /// belongs to that evening, not to the next morning, and bucketing by UTC
    /// would split most of the clan's play sessions across two bars.
    /// </param>
    public async Task<byte[]?> TryRenderPlaytimeChartAsync(
        int days, TimeZoneInfo zone, string subtitle, CancellationToken ct = default)
    {
        try
        {
            days = Math.Clamp(days, 2, 90);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            var lastDay = DateOnly.FromDateTime(nowLocal);
            var firstDay = lastDay.AddDays(-(days - 1));

            var windowStartUtc = LocalMidnightToUtc(firstDay, zone);
            var windowEndUtc = LocalMidnightToUtc(lastDay.AddDays(1), zone);

            var sessions = await db.SatisfactorySessions
                .AsNoTracking()
                .Where(s => s.StartedUtc < windowEndUtc && (s.EndedUtc ?? s.LastSeenUtc) > windowStartUtc)
                .ToListAsync(ct);

            if (sessions.Count == 0)
            {
                _logger.LogDebug("Skipping Satisfactory playtime chart: no sessions in the last {Days} days", days);
                return null;
            }

            var dayBounds = new List<(DateOnly Day, DateTime StartUtc, DateTime EndUtc)>();
            for (var i = 0; i < days; i++)
            {
                var d = firstDay.AddDays(i);
                dayBounds.Add((d, LocalMidnightToUtc(d, zone), LocalMidnightToUtc(d.AddDays(1), zone)));
            }

            // Clip each session into every local day it touches, the same way
            // the daily digest does. A session spanning midnight contributes to
            // both days in proportion, not wholly to one.
            var byPlayer = new Dictionary<string, double[]>(StringComparer.Ordinal);

            foreach (var s in sessions)
            {
                var extent = s.EndedUtc ?? s.LastSeenUtc;

                if (!byPlayer.TryGetValue(s.PlayerName, out var row))
                {
                    row = new double[days];
                    byPlayer[s.PlayerName] = row;
                }

                for (var i = 0; i < days; i++)
                {
                    var (_, dayStart, dayEnd) = dayBounds[i];

                    var start = s.StartedUtc > dayStart ? s.StartedUtc : dayStart;
                    var end = extent < dayEnd ? extent : dayEnd;

                    var overlap = end - start;
                    if (overlap > TimeSpan.Zero) row[i] += overlap.TotalHours;
                }
            }

            // Biggest contributor at the bottom of every stack — bars that
            // reshuffle their own ordering between days are unreadable.
            var ranked = byPlayer
                .Select(kv => new PlayerSeries(kv.Key, kv.Value, kv.Value.Sum()))
                .Where(p => p.Total > 0)
                .OrderByDescending(p => p.Total)
                .ToList();

            if (ranked.Count == 0) return null;

            var players = Collapse(ranked, PaletteSize - 1, days);

            ct.ThrowIfCancellationRequested();

            return RenderPlaytimeChart(players, dayBounds.Select(b => b.Day).ToList(), subtitle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render the Satisfactory playtime chart");
            return null;
        }
    }

    private sealed record PlayerSeries(string Name, double[] Hours, double Total);

    /// <summary>
    /// Keeps the top <paramref name="keep"/> players and sums everyone else into
    /// a single "others" series, so the number of stacked bands never exceeds
    /// the palette. Past that the colours would wrap — player 7 drawn in player
    /// 1's colour, with the legend positively misattributing a segment.
    /// </summary>
    private static List<PlayerSeries> Collapse(List<PlayerSeries> ranked, int keep, int days)
    {
        if (ranked.Count <= keep) return ranked;

        var kept = ranked.Take(keep).ToList();
        var rest = ranked.Skip(keep).ToList();

        var combined = new double[days];
        foreach (var p in rest)
            for (var i = 0; i < days; i++)
                combined[i] += p.Hours[i];

        kept.Add(new PlayerSeries($"{rest.Count} others", combined, combined.Sum()));
        return kept;
    }

    private byte[] RenderPlaytimeChart(
        IReadOnlyList<PlayerSeries> players, IReadOnlyList<DateOnly> days, string subtitle)
    {
        var dayCount = days.Count;

        var totals = new double[dayCount];
        for (var i = 0; i < dayCount; i++)
            totals[i] = players.Sum(p => p.Hours[i]);

        var (yMax, yStep) = NiceAxis(totals.Max());

        const float plotLeft = MarginLeft;
        const float plotRight = CanvasWidth - MarginRight;
        const float plotTop = MarginTop;
        const float plotBottom = CanvasHeight - MarginBottom;
        const float plotWidth = plotRight - plotLeft;
        const float plotHeight = plotBottom - plotTop;

        var slotWidth = plotWidth / dayCount;
        var barGap = Math.Min(14f, slotWidth * 0.3f);
        var barWidth = slotWidth - barGap;
        var radius = Math.Min(7f, barWidth / 2f);

        float YToPixel(double y) => plotBottom - (float)(y / yMax) * plotHeight;

        var info = new SKImageInfo(CanvasWidth, CanvasHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        DrawBackground(canvas);
        DrawPanel(canvas, plotLeft, plotTop, plotRight, plotBottom);

        using var titleFont = Font(26, bold: true);
        using var subtitleFont = Font(14);
        using var labelFont = Font(14);
        using var totalFont = Font(13, bold: true);

        // Weekend columns, tinted before anything else so they read as
        // background rather than data.
        using (var weekend = new SKPaint { Color = Hex(PanelHex, 10), IsAntialias = true })
        {
            for (var i = 0; i < dayCount; i++)
            {
                var dow = days[i].DayOfWeek;
                if (dow is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) continue;

                var x0 = plotLeft + i * slotWidth;
                canvas.DrawRect(new SKRect(x0, plotTop, x0 + slotWidth, plotBottom), weekend);
            }
        }

        DrawYAxis(canvas, labelFont, yMax, yStep, plotLeft, plotRight, YToPixel, FormatHours);

        // The blur never changes, so it's built once rather than per bar. A
        // 90-day chart was allocating 90 identical native filters, none of them
        // disposed. The paint takes its own native reference, so disposing this
        // after the loop is correct.
        using var barBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);

        for (var i = 0; i < dayCount; i++)
        {
            if (totals[i] <= 0) continue;

            var x0 = plotLeft + i * slotWidth + barGap / 2f;
            var stackTop = YToPixel(totals[i]);
            var silhouette = new SKRect(x0, stackTop, x0 + barWidth, plotBottom);

            using (var glow = new SKPaint
            {
                Color = Hex(PlayerHiHex[0], 26),
                IsAntialias = true,
                MaskFilter = barBlur,
            })
            {
                canvas.DrawRoundRect(silhouette, radius, radius, glow);
            }

            // Rounded top on the stack AS A WHOLE, not per segment: clip once to
            // the silhouette, then draw square segments inside it. Rounding each
            // segment would put a notch between every player's band.
            canvas.Save();
            using (var clip = new SKRoundRect(silhouette, radius, radius))
            {
                canvas.ClipRoundRect(clip, SKClipOperation.Intersect, antialias: true);
            }

            var bottom = plotBottom;

            for (var p = 0; p < players.Count; p++)
            {
                var hours = players[p].Hours[i];
                if (hours <= 0) continue;

                var top = bottom - (float)(hours / yMax) * plotHeight;

                using (var shader = SKShader.CreateLinearGradient(
                           new SKPoint(x0, bottom), new SKPoint(x0, top),
                           new[] { Hex(PlayerLoHex[p]), Hex(PlayerHiHex[p]) },
                           SKShaderTileMode.Clamp))
                using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
                {
                    canvas.DrawRect(new SKRect(x0, top, x0 + barWidth, bottom), paint);
                }

                // Hairline between segments so adjacent bands stay distinct even
                // when two players' colours are close in value.
                using (var divider = new SKPaint
                {
                    Color = Hex(BgBottomHex, 120),
                    StrokeWidth = 1.5f,
                    IsAntialias = true,
                })
                {
                    canvas.DrawLine(x0, top, x0 + barWidth, top, divider);
                }

                bottom = top;
            }

            // Highlight along the crown. Two pixels of near-white at low alpha
            // is the difference between a flat block and a lit one.
            using (var highlight = new SKPaint
            {
                Color = Hex(AxisHex, 110),
                StrokeWidth = 2.4f,
                IsAntialias = true,
            })
            {
                canvas.DrawLine(x0 + 1f, stackTop + 1.2f, x0 + barWidth - 1f, stackTop + 1.2f, highlight);
            }

            canvas.Restore();

            using (var totalPaint = new SKPaint { Color = Hex(AxisHex, 170), IsAntialias = true })
            {
                var label = FormatHours(totals[i]);
                canvas.DrawText(label,
                    x0 + barWidth / 2f - totalFont.MeasureText(label) / 2f, stackTop - 10f,
                    totalFont, totalPaint);
            }
        }

        // ── X-axis day labels ──
        using (var labelPaint = new SKPaint { Color = Hex(AxisHex, 150), IsAntialias = true })
        {
            var step = Math.Max(1, (int)Math.Ceiling(dayCount / (double)XAxisTickCount));

            for (var i = 0; i < dayCount; i += step)
            {
                var label = days[i].ToDateTime(TimeOnly.MinValue).ToString("MMM d");
                var x = plotLeft + i * slotWidth + slotWidth / 2f;
                canvas.DrawText(label,
                    x - labelFont.MeasureText(label) / 2f, plotBottom + 30f,
                    labelFont, labelPaint);
            }
        }

        DrawHeader(canvas, titleFont, subtitleFont, "Playtime", subtitle);

        // Collapse() guarantees players.Count <= palette length, so every
        // stacked band gets its own swatch and no colour is reused.
        DrawLegend(canvas, labelFont, players
            .Select((p, i) => (PlayerLoHex[i], PlayerHiHex[i], $"{p.Name}  {p.Total:0}h"))
            .ToArray());

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    // ─── Chrome ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Vertical gradient plus a soft radial lift behind the plot, so the canvas
    /// reads as a lit panel rather than a flat black rectangle.
    /// </summary>
    private static void DrawBackground(SKCanvas canvas)
    {
        var full = new SKRect(0, 0, CanvasWidth, CanvasHeight);

        using (var shader = SKShader.CreateLinearGradient(
                   new SKPoint(0, 0), new SKPoint(0, CanvasHeight),
                   new[] { Hex(BgTopHex), Hex(BgBottomHex) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(full, paint);
        }

        using (var shader = SKShader.CreateRadialGradient(
                   new SKPoint(CanvasWidth * 0.5f, CanvasHeight * 0.42f), CanvasWidth * 0.62f,
                   new[] { Hex(LiftHex, 90), Hex(LiftHex, 0) },
                   SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(full, paint);
        }
    }

    private static void DrawPanel(SKCanvas canvas, float left, float top, float right, float bottom)
    {
        var rect = new SKRect(left - 14f, top - 14f, right + 14f, bottom + 14f);

        using (var fill = new SKPaint { Color = Hex(PanelHex, 8), IsAntialias = true })
        {
            canvas.DrawRoundRect(rect, 14f, 14f, fill);
        }

        using (var border = new SKPaint
        {
            Color = Hex(PanelHex, 18),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        })
        {
            canvas.DrawRoundRect(rect, 14f, 14f, border);
        }
    }

    /// <summary>
    /// Gridlines every <paramref name="step"/>, rather than slicing the maximum
    /// into a fixed count. Slicing produced gridlines at 64/128/192 on a 320 MW
    /// axis — correct, but nobody reads "64 MW" as a round number.
    /// </summary>
    private static void DrawYAxis(
        SKCanvas canvas, SKFont font, double yMax, double step,
        float left, float right, Func<double, float> yToPixel, Func<double, string> format)
    {
        using var dash = SKPathEffect.CreateDash(new[] { 2f, 6f }, 0f);
        using var grid = new SKPaint
        {
            Color = Hex(GridHex, 26),
            StrokeWidth = 1f,
            Style = SKPaintStyle.Stroke,
            PathEffect = dash,
            IsAntialias = true,
        };
        using var label = new SKPaint { Color = Hex(AxisHex, 150), IsAntialias = true };

        var metrics = font.Metrics;
        var verticalCenter = -(metrics.Ascent + metrics.Descent) / 2f;

        // Hard ceiling on the iteration count. NiceAxis never needs more than
        // about six, so anything approaching this is a degenerate axis, and an
        // unbounded loop here would wedge the render task rather than produce a
        // bad chart. The epsilon is RELATIVE: an absolute 1e-9 made a tiny axis
        // (a one-tick session overlap is ~3e-11 hours) iterate for ages.
        for (var g = 0; g <= 64; g++)
        {
            var value = step * g;
            if (value > yMax + Math.Abs(yMax) * 1e-9) break;

            var y = yToPixel(value);
            canvas.DrawLine(left, y, right, y, grid);

            var text = format(value);
            canvas.DrawText(text, left - 14f - font.MeasureText(text), y + verticalCenter, font, label);
        }
    }

    private static void DrawHeader(
        SKCanvas canvas, SKFont titleFont, SKFont subtitleFont, string title, string subtitle)
    {
        using (var paint = new SKPaint { Color = Hex(AxisHex, 240), IsAntialias = true })
        {
            canvas.DrawText(title, MarginLeft - 14f, 46f, titleFont, paint);
        }

        if (string.IsNullOrWhiteSpace(subtitle)) return;

        using (var paint = new SKPaint { Color = Hex(AxisHex, 110), IsAntialias = true })
        {
            canvas.DrawText(subtitle, MarginLeft - 14f, 70f, subtitleFont, paint);
        }
    }

    /// <summary>
    /// Rounded gradient swatches, laid out right-to-left from the header line.
    ///
    /// <para>It grows leftward, so a long enough legend WOULD eventually reach
    /// the left-aligned title. Six entries of player names fits comfortably,
    /// and Collapse() caps it at six — but that's the bound, not the layout.</para>
    /// </summary>
    private static void DrawLegend(
        SKCanvas canvas, SKFont font, IReadOnlyList<(string Lo, string Hi, string Label)> entries)
    {
        const float swatch = 14f;
        const float gap = 9f;
        const float itemGap = 24f;
        const float y = 40f;

        var x = (float)(CanvasWidth - MarginRight);

        using var labelPaint = new SKPaint { Color = Hex(AxisHex, 200), IsAntialias = true };

        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var (lo, hi, text) = entries[i];

            x -= font.MeasureText(text);
            canvas.DrawText(text, x, y + 10f, font, labelPaint);

            x -= gap + swatch;
            var rect = new SKRect(x, y, x + swatch, y + swatch);

            using (var shader = SKShader.CreateLinearGradient(
                       new SKPoint(x, y + swatch), new SKPoint(x, y),
                       new[] { Hex(lo), Hex(hi) },
                       SKShaderTileMode.Clamp))
            using (var paint = new SKPaint { Shader = shader, IsAntialias = true })
            {
                canvas.DrawRoundRect(rect, 4f, 4f, paint);
            }

            x -= itemGap;
        }
    }

    // ─── Geometry ────────────────────────────────────────────────────────────

    /// <summary>Typical spacing between samples, used to decide what a gap is.</summary>
    private static TimeSpan MedianInterval(IReadOnlyList<SatisfactoryMetricSample> samples)
    {
        if (samples.Count < 2) return TimeSpan.FromMinutes(2);

        var deltas = new List<double>(samples.Count - 1);
        for (var i = 1; i < samples.Count; i++)
            deltas.Add((samples[i].SampledUtc - samples[i - 1].SampledUtc).TotalSeconds);

        deltas.Sort();

        var median = deltas[deltas.Count / 2];
        return median > 0 ? TimeSpan.FromSeconds(median) : TimeSpan.FromMinutes(2);
    }

    /// <summary>Contiguous index ranges, split wherever sampling stopped.</summary>
    private static List<(int Start, int End)> Runs(
        IReadOnlyList<SatisfactoryMetricSample> samples, TimeSpan gap)
    {
        var runs = new List<(int, int)>();
        var start = 0;

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].SampledUtc - samples[i - 1].SampledUtc <= gap) continue;
            runs.Add((start, i - 1));
            start = i;
        }

        runs.Add((start, samples.Count - 1));
        return runs;
    }

    /// <summary>
    /// Quadratic mid-point smoothing, one subpath per contiguous run — the same
    /// curve shape as <see cref="MemberActivityChartRenderer"/>'s trend line.
    ///
    /// <para>Breaking on gaps is the point. Spacing points by time stops an
    /// outage being squeezed to the width of a normal interval, but on its own
    /// the path still draws straight from the last reading before the gap to the
    /// first one after — inventing a smooth multi-hour trend out of two data
    /// points, across the widest part of the chart.</para>
    /// </summary>
    /// <param name="closeTo">
    /// Baseline Y to close each run down to, for a filled area; null for a
    /// stroke-only path.
    /// </param>
    private static SKPath SmoothPath(
        IReadOnlyList<SatisfactoryMetricSample> samples,
        IReadOnlyList<double> values,
        IReadOnlyList<(int Start, int End)> runs,
        Func<DateTime, float> xToPixel,
        Func<double, float> yToPixel,
        float? closeTo)
    {
        var path = new SKPath();

        foreach (var (start, end) in runs)
        {
            if (end <= start) continue;

            var count = end - start + 1;
            var xs = new float[count];
            var ys = new float[count];

            for (var i = 0; i < count; i++)
            {
                xs[i] = xToPixel(samples[start + i].SampledUtc);
                ys[i] = yToPixel(values[start + i]);
            }

            path.MoveTo(xs[0], ys[0]);

            if (count == 2)
            {
                path.LineTo(xs[1], ys[1]);
            }
            else
            {
                for (var k = 1; k < count - 1; k++)
                    path.QuadTo(xs[k], ys[k], (xs[k] + xs[k + 1]) / 2f, (ys[k] + ys[k + 1]) / 2f);

                path.LineTo(xs[^1], ys[^1]);
            }

            if (closeTo is { } baseline)
            {
                path.LineTo(xs[^1], baseline);
                path.LineTo(xs[0], baseline);
                path.Close();
            }
        }

        return path;
    }

    private static SKPaint Glow(string hex, byte alpha, float width, float sigma) => new()
    {
        Color = Hex(hex, alpha),
        StrokeWidth = width,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        IsAntialias = true,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma),
    };

    private static DateTime LocalMidnightToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// Picks a round gridline step, then the smallest ceiling that is a whole
    /// number of them.
    ///
    /// <para>Rounding the MAXIMUM up a 1/2/2.5/5/10 ladder wastes plot height —
    /// a 300 MW capacity became a 500 MW axis, throwing away 40% of the chart.
    /// Rounding the STEP instead gives 320 with lines every 80.</para>
    /// </summary>
    private static (double Max, double Step) NiceAxis(double value, int targetSteps = 5)
    {
        // A NaN or infinite reading would produce a NaN step, and the gridline
        // loop's `value > yMax` test is false for NaN — so it would never end.
        if (!double.IsFinite(value) || value <= 0) return (1, 1);

        var raw = value / targetSteps;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var normalized = raw / magnitude;

        double nice = 10;
        foreach (var candidate in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
        {
            if (normalized > candidate) continue;
            nice = candidate;
            break;
        }

        var step = nice * magnitude;
        return (step * Math.Ceiling(value / step), step);
    }

    private static string FormatHours(double hours) =>
        Math.Abs(hours - Math.Floor(hours)) < 0.05 ? $"{(int)Math.Round(hours)}h" : $"{hours:0.#}h";

    private static SKFont Font(float size, bool bold = false)
    {
        // Null family name = system default, which on the bot's Linux image is
        // DejaVu Sans (fonts-dejavu-core, installed by the Dockerfile). The
        // fallback matters: SKTypeface.Default is a shared singleton, so it is
        // deliberately never disposed. SKFont owns scaling state and IS disposed
        // by callers.
        var typeface = SKTypeface.FromFamilyName(null, bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                       ?? SKTypeface.Default;

        return new SKFont(typeface, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
    }

    private static SKColor Hex(string hex, byte alpha = 255) => SKColor.Parse(hex).WithAlpha(alpha);
}
