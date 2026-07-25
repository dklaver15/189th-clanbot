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
/// <item><b>Power</b> — consumption against capacity over time, with fuse trips
/// marked. Answers "was that blown fuse coming?", which a live reading can't.</item>
/// <item><b>Playtime</b> — hours per player per day, stacked.</item>
/// </list>
///
/// ── Why SkiaSharp directly, not ScottPlot ──
/// Following <see cref="MemberActivityChartRenderer"/>, which documents the
/// reasoning: ScottPlot's 5.0 → 5.1 refactor moved the styling APIs its own
/// cookbook documents, and a guess that threw at runtime produced a silently
/// missing chart rather than a build error. SkiaSharp is what ScottPlot draws
/// with anyway. The palette and layout constants here deliberately match that
/// renderer so the bot's charts look like one family.
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

    // ── Palette, matching MemberActivityChartRenderer ────────────────────────
    private const string BackgroundHex = "0d1017";   // deep console black
    private const string AxisColorHex  = "ffffff";   // labels & title
    private const string GridColorHex  = "ffd86b";   // gridlines, very low alpha
    private const string ConsumedHex   = "5865f2";   // Discord blurple — draw
    private const string CapacityHex   = "ffd86b";   // command-deck gold — capacity
    private const string TrippedHex    = "ed4245";   // Discord red — fuse trips
    private const string BatteryHex    = "3ba55d";   // Discord green — battery charge

    /// <summary>
    /// Per-player bar colours. Deliberately a fixed cycle rather than a hash of
    /// the name: with four players, a hash would occasionally hand two of them
    /// near-identical colours and there's no way to override it.
    /// </summary>
    private static readonly string[] PlayerHexes =
    {
        "5865f2", "ffd86b", "3ba55d", "eb459e", "00b0f4", "f47b67",
    };

    // ── Layout ───────────────────────────────────────────────────────────────
    private const int CanvasWidth  = 1800;
    private const int CanvasHeight = 600;
    private const int MarginLeft   = 90;
    private const int MarginRight  = 30;
    private const int MarginTop    = 70;
    private const int MarginBottom = 70;

    private const int YAxisGridSteps = 5;
    private const int XAxisTickCount = 7;

    public SatisfactoryChartRenderer(
        IServiceProvider services,
        ILogger<SatisfactoryChartRenderer> logger)
    {
        _services = services;
        _logger = logger;
    }

    // ─── Power ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Grid load over the given window: consumption as a filled line against a
    /// capacity line, with fuse trips as red markers along the top.
    /// </summary>
    /// <returns>PNG bytes, or null if there's nothing worth drawing.</returns>
    public async Task<byte[]?> TryRenderPowerChartAsync(
        TimeSpan window, string title, CancellationToken ct = default)
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

            return RenderPowerChart(samples, title);
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

    private byte[] RenderPowerChart(IReadOnlyList<SatisfactoryMetricSample> samples, string title)
    {
        var consumed = samples.Select(s => s.PowerConsumedMw).ToArray();
        var capacity = samples.Select(s => s.PowerCapacityMw).ToArray();

        var yMax = NiceCeiling(Math.Max(consumed.Max(), capacity.Max()));

        const float plotLeft   = MarginLeft;
        const float plotRight  = CanvasWidth - MarginRight;
        const float plotTop    = MarginTop;
        const float plotBottom = CanvasHeight - MarginBottom;
        const float plotWidth  = plotRight - plotLeft;
        const float plotHeight = plotBottom - plotTop;

        // Samples are plotted against TIME, not against their index. The poll
        // can miss ticks (FRM down, bot restarting), and index-spacing would
        // quietly compress a six-hour outage into the same width as six
        // minutes — making the chart lie about when things happened.
        var t0 = samples[0].SampledUtc;
        var span = samples[^1].SampledUtc - t0;
        if (span <= TimeSpan.Zero) span = TimeSpan.FromMinutes(1);

        float XToPixel(DateTime t) =>
            plotLeft + (float)((t - t0).TotalSeconds / span.TotalSeconds) * plotWidth;
        float YToPixel(double y) => plotBottom - (float)(y / yMax) * plotHeight;

        var info = new SKImageInfo(CanvasWidth, CanvasHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(BackgroundHex));

        var titleTypeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        var bodyTypeface  = SKTypeface.FromFamilyName(null) ?? SKTypeface.Default;
        using var titleFont = new SKFont(titleTypeface, 22f);
        using var labelFont = new SKFont(bodyTypeface, 14f);

        var labelMetrics = labelFont.Metrics;
        var labelVerticalCenter = -(labelMetrics.Ascent + labelMetrics.Descent) / 2f;

        DrawYAxis(canvas, labelFont, labelVerticalCenter, yMax, plotLeft, plotRight, YToPixel, v => $"{v:0.#} MW");

        // ── Consumption: area fill, then line ──
        using (var fillPath = BuildAreaPath(samples, consumed, XToPixel, YToPixel, plotBottom))
        using (var fillShader = SKShader.CreateLinearGradient(
                   new SKPoint(0, plotBottom),
                   new SKPoint(0, plotTop),
                   new[] { SKColor.Parse(ConsumedHex).WithAlpha(110), SKColor.Parse(ConsumedHex).WithAlpha(0) },
                   SKShaderTileMode.Clamp))
        using (var fillPaint = new SKPaint { Shader = fillShader, Style = SKPaintStyle.Fill, IsAntialias = true })
        {
            canvas.DrawPath(fillPath, fillPaint);
        }

        using (var linePath = BuildLinePath(samples, consumed, XToPixel, YToPixel))
        using (var linePaint = new SKPaint
        {
            Color = SKColor.Parse(ConsumedHex),
            StrokeWidth = 3f,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(linePath, linePaint);
        }

        // ── Capacity: dashed, because it's a ceiling rather than a measurement ──
        using (var capPath = BuildLinePath(samples, capacity, XToPixel, YToPixel))
        using (var dash = SKPathEffect.CreateDash(new[] { 10f, 6f }, 0f))
        using (var capPaint = new SKPaint
        {
            Color = SKColor.Parse(CapacityHex).WithAlpha(200),
            StrokeWidth = 2.5f,
            Style = SKPaintStyle.Stroke,
            PathEffect = dash,
            IsAntialias = true,
        })
        {
            canvas.DrawPath(capPath, capPaint);
        }

        // ── Fuse trips ──
        // Drawn as a full-height band rather than a dot on the line: a trip
        // takes consumption to zero, so a marker at the data point would sit at
        // the bottom of the chart, exactly where it's least visible.
        using (var tripPaint = new SKPaint
        {
            Color = SKColor.Parse(TrippedHex).WithAlpha(70),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        })
        {
            foreach (var s in samples.Where(s => s.TrippedCount > 0))
            {
                var x = XToPixel(s.SampledUtc);
                canvas.DrawRect(new SKRect(x - 1.5f, plotTop, x + 1.5f, plotBottom), tripPaint);
            }
        }

        // ── Battery charge, only when the grid actually has batteries ──
        // Plotted on the same axis as a percentage OF yMax, so it reads as a
        // shape rather than a value — labelling it would need a second axis and
        // the chart is already carrying three series.
        if (samples.Any(s => s.BatteryPercent is > 0))
        {
            var battery = samples.Select(s => (s.BatteryPercent ?? 0) / 100.0 * yMax).ToArray();

            using var batteryPath = BuildLinePath(samples, battery, XToPixel, YToPixel);
            using var batteryPaint = new SKPaint
            {
                Color = SKColor.Parse(BatteryHex).WithAlpha(180),
                StrokeWidth = 2f,
                Style = SKPaintStyle.Stroke,
                IsAntialias = true,
            };
            canvas.DrawPath(batteryPath, batteryPaint);
        }

        DrawTimeAxis(canvas, labelFont, samples.Select(s => s.SampledUtc).ToList(), XToPixel, plotBottom, span);
        DrawTitle(canvas, titleFont, title);

        DrawLegend(canvas, labelFont, plotRight, plotTop, new[]
        {
            (ConsumedHex, "Draw"),
            (CapacityHex, "Capacity"),
            (TrippedHex,  "Fuse tripped"),
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
        int days, TimeZoneInfo zone, string title, CancellationToken ct = default)
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

            // Bucket by clipping each session into every local day it touches,
            // the same way the daily digest does. A session spanning midnight
            // contributes to both days in proportion, not wholly to one.
            var dayBounds = new List<(DateOnly Day, DateTime StartUtc, DateTime EndUtc)>();
            for (var i = 0; i < days; i++)
            {
                var d = firstDay.AddDays(i);
                dayBounds.Add((d, LocalMidnightToUtc(d, zone), LocalMidnightToUtc(d.AddDays(1), zone)));
            }

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

            // Order players by total time so the biggest contributor is the
            // bottom of every stack — bars that reshuffle their own ordering
            // between days are unreadable.
            var ranked = byPlayer
                .Select(kv => new PlayerSeries(kv.Key, kv.Value, kv.Value.Sum()))
                .Where(p => p.Total > 0)
                .OrderByDescending(p => p.Total)
                .ToList();

            if (ranked.Count == 0) return null;

            // Never stack more distinct players than the palette can label. Past
            // that the colours wrap, so player 7 gets player 1's swatch and the
            // legend positively misattributes a segment. Folding the tail into
            // one "Others" band is honest and keeps the totals right.
            var players = Collapse(ranked, PlayerHexes.Length - 1, days);

            ct.ThrowIfCancellationRequested();

            return RenderPlaytimeChart(players, dayBounds.Select(b => b.Day).ToList(), title);
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
    /// a single "Others" series, so the number of stacked bands never exceeds
    /// the palette.
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
        IReadOnlyList<PlayerSeries> players, IReadOnlyList<DateOnly> days, string title)
    {
        var dayCount = days.Count;

        // Stack height per day is the sum across players.
        var totals = new double[dayCount];
        for (var i = 0; i < dayCount; i++)
            totals[i] = players.Sum(p => p.Hours[i]);

        var yMax = NiceCeiling(totals.Max());

        const float plotLeft   = MarginLeft;
        const float plotRight  = CanvasWidth - MarginRight;
        const float plotTop    = MarginTop;
        const float plotBottom = CanvasHeight - MarginBottom;
        const float plotWidth  = plotRight - plotLeft;
        const float plotHeight = plotBottom - plotTop;

        var slotWidth = plotWidth / dayCount;
        var barGap = Math.Min(6f, slotWidth * 0.25f);
        var barWidth = slotWidth - barGap;

        float YToPixel(double y) => plotBottom - (float)(y / yMax) * plotHeight;

        var info = new SKImageInfo(CanvasWidth, CanvasHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColor.Parse(BackgroundHex));

        var titleTypeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        var bodyTypeface  = SKTypeface.FromFamilyName(null) ?? SKTypeface.Default;
        using var titleFont = new SKFont(titleTypeface, 22f);
        using var labelFont = new SKFont(bodyTypeface, 14f);

        var labelMetrics = labelFont.Metrics;
        var labelVerticalCenter = -(labelMetrics.Ascent + labelMetrics.Descent) / 2f;

        DrawYAxis(canvas, labelFont, labelVerticalCenter, yMax, plotLeft, plotRight, YToPixel, FormatHours);

        // ── Stacked bars ──
        for (var i = 0; i < dayCount; i++)
        {
            var x0 = plotLeft + i * slotWidth + barGap / 2f;
            var runningBottom = plotBottom;

            for (var p = 0; p < players.Count; p++)
            {
                var hours = players[p].Hours[i];
                if (hours <= 0) continue;

                var segmentHeight = (float)(hours / yMax) * plotHeight;
                var top = runningBottom - segmentHeight;

                using var paint = new SKPaint
                {
                    Color = SKColor.Parse(PlayerHexes[p % PlayerHexes.Length]).WithAlpha(225),
                    Style = SKPaintStyle.Fill,
                    IsAntialias = true,
                };
                canvas.DrawRect(new SKRect(x0, top, x0 + barWidth, runningBottom), paint);

                runningBottom = top;
            }
        }

        // ── X-axis day labels ──
        // Thinned to at most XAxisTickCount so a 30-day chart doesn't render
        // overlapping dates.
        using (var labelPaint = new SKPaint { Color = SKColor.Parse(AxisColorHex), IsAntialias = true })
        {
            var step = Math.Max(1, (int)Math.Ceiling(dayCount / (double)XAxisTickCount));
            var baselineY = plotBottom + 22f;

            for (var i = 0; i < dayCount; i += step)
            {
                var label = days[i].ToDateTime(TimeOnly.MinValue).ToString("MMM d");
                var x = plotLeft + i * slotWidth + slotWidth / 2f;
                var w = labelFont.MeasureText(label);
                canvas.DrawText(label, x - w / 2f, baselineY, labelFont, labelPaint);
            }
        }

        DrawTitle(canvas, titleFont, title);

        // Collapse() guarantees players.Count <= PlayerHexes.Length, so every
        // stacked band gets a swatch and no colour is reused.
        DrawLegend(canvas, labelFont, plotRight, plotTop,
            players.Select((p, i) => (PlayerHexes[i], p.Name)).ToArray());

        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    // ─── Shared drawing ──────────────────────────────────────────────────────

    private static void DrawYAxis(
        SKCanvas canvas, SKFont labelFont, float verticalCenter, double yMax,
        float plotLeft, float plotRight, Func<double, float> yToPixel, Func<double, string> format)
    {
        using var gridPaint = new SKPaint
        {
            Color = SKColor.Parse(GridColorHex).WithAlpha(20),
            StrokeWidth = 1,
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
        };
        using var labelPaint = new SKPaint { Color = SKColor.Parse(AxisColorHex), IsAntialias = true };

        for (var g = 0; g <= YAxisGridSteps; g++)
        {
            var value = yMax * g / YAxisGridSteps;
            var y = yToPixel(value);

            canvas.DrawLine(plotLeft, y, plotRight, y, gridPaint);

            var label = format(value);
            var w = labelFont.MeasureText(label);
            canvas.DrawText(label, plotLeft - 10f - w, y + verticalCenter, labelFont, labelPaint);
        }
    }

    /// <summary>
    /// Time labels along the bottom. The format adapts to the window: a 24-hour
    /// chart labelled "Jul 25" seven times says nothing, and a 30-day chart
    /// labelled by the hour says less.
    /// </summary>
    private static void DrawTimeAxis(
        SKCanvas canvas, SKFont labelFont, IReadOnlyList<DateTime> times,
        Func<DateTime, float> xToPixel, float plotBottom, TimeSpan span)
    {
        var format = span <= TimeSpan.FromHours(36) ? "HH:mm" : "MMM d";

        using var labelPaint = new SKPaint { Color = SKColor.Parse(AxisColorHex), IsAntialias = true };
        var baselineY = plotBottom + 22f;

        // Never ask for more ticks than there are samples, or several ticks
        // resolve to the same index and the same label is drawn on top of
        // itself — which renders as one oddly bold, slightly fuzzy date.
        var ticks = Math.Min(XAxisTickCount, times.Count);
        if (ticks < 2) return;

        for (var t = 0; t < ticks; t++)
        {
            var index = (int)Math.Round((double)t / (ticks - 1) * (times.Count - 1));
            var time = times[index];
            var label = time.ToString(format);
            var x = xToPixel(time);
            var w = labelFont.MeasureText(label);
            canvas.DrawText(label, x - w / 2f, baselineY, labelFont, labelPaint);
        }

        // The axis is UTC — say so, rather than leaving people to guess whether
        // a 03:00 spike means someone was up at 3am.
        using var noteFont = new SKFont(SKTypeface.FromFamilyName(null) ?? SKTypeface.Default, 12f);
        using var notePaint = new SKPaint { Color = SKColor.Parse(AxisColorHex).WithAlpha(120), IsAntialias = true };
        if (format == "HH:mm")
            canvas.DrawText("times UTC", MarginLeft, plotBottom + 44f, noteFont, notePaint);
    }

    private static void DrawTitle(SKCanvas canvas, SKFont titleFont, string title)
    {
        using var paint = new SKPaint { Color = SKColor.Parse(AxisColorHex), IsAntialias = true };
        var w = titleFont.MeasureText(title);
        canvas.DrawText(title, (CanvasWidth - w) / 2f, 42f, titleFont, paint);
    }

    /// <summary>
    /// Swatch-and-label legend, laid out right-to-left from the plot's top-right
    /// corner so it never collides with the title.
    /// </summary>
    private static void DrawLegend(
        SKCanvas canvas, SKFont font, float plotRight, float plotTop,
        IReadOnlyList<(string Hex, string Label)> entries)
    {
        const float swatch = 12f;
        const float gap = 8f;
        const float itemGap = 22f;

        var x = plotRight;
        var y = plotTop - 18f;

        using var labelPaint = new SKPaint { Color = SKColor.Parse(AxisColorHex).WithAlpha(210), IsAntialias = true };

        foreach (var (hex, label) in entries.Reverse())
        {
            var textWidth = font.MeasureText(label);
            x -= textWidth;
            canvas.DrawText(label, x, y + swatch - 2f, font, labelPaint);

            x -= gap + swatch;
            using var swatchPaint = new SKPaint
            {
                Color = SKColor.Parse(hex),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            };
            canvas.DrawRect(new SKRect(x, y, x + swatch, y + swatch), swatchPaint);

            x -= itemGap;
        }
    }

    /// <summary>
    /// Traces the series, <b>breaking the line across sampling gaps</b>.
    ///
    /// <para>Spacing the points by time stops an outage being compressed to the
    /// width of a normal interval, but on its own it still draws a straight
    /// LineTo from the last reading before the gap to the first one after —
    /// inventing a smooth six-hour trend across the widest part of the chart out
    /// of two data points. Starting a new subpath instead leaves the gap
    /// visible, which is the honest rendering of "we don't know".</para>
    /// </summary>
    private static SKPath BuildLinePath(
        IReadOnlyList<SatisfactoryMetricSample> samples, IReadOnlyList<double> values,
        Func<DateTime, float> xToPixel, Func<double, float> yToPixel)
    {
        var path = new SKPath();
        if (values.Count == 0) return path;

        var gapThreshold = MedianInterval(samples) * 3;

        path.MoveTo(xToPixel(samples[0].SampledUtc), yToPixel(values[0]));

        for (var i = 1; i < values.Count; i++)
        {
            var x = xToPixel(samples[i].SampledUtc);
            var y = yToPixel(values[i]);

            if (samples[i].SampledUtc - samples[i - 1].SampledUtc > gapThreshold)
                path.MoveTo(x, y);
            else
                path.LineTo(x, y);
        }

        return path;
    }

    /// <summary>
    /// Typical spacing between samples, used to decide what counts as a gap.
    /// Derived from the data rather than read from config, so it stays correct
    /// if the poll interval is changed or the window spans a change.
    /// </summary>
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

    /// <summary>
    /// The filled area under the series, as one closed shape PER contiguous
    /// run of samples.
    ///
    /// <para>Built independently rather than by extending the stroke path: with
    /// gaps the stroke path has several subpaths, and closing that would drop a
    /// baseline only under the last one while leaving the earlier runs open —
    /// filling a shape that spans the gaps it was just taught to avoid.</para>
    /// </summary>
    private static SKPath BuildAreaPath(
        IReadOnlyList<SatisfactoryMetricSample> samples, IReadOnlyList<double> values,
        Func<DateTime, float> xToPixel, Func<double, float> yToPixel, float baselineY)
    {
        var path = new SKPath();
        if (values.Count == 0) return path;

        var gapThreshold = MedianInterval(samples) * 3;

        var runStart = 0;

        void CloseRun(int endIndex)
        {
            // A single isolated sample has no area to fill.
            if (endIndex <= runStart) return;

            path.MoveTo(xToPixel(samples[runStart].SampledUtc), yToPixel(values[runStart]));
            for (var i = runStart + 1; i <= endIndex; i++)
                path.LineTo(xToPixel(samples[i].SampledUtc), yToPixel(values[i]));

            path.LineTo(xToPixel(samples[endIndex].SampledUtc), baselineY);
            path.LineTo(xToPixel(samples[runStart].SampledUtc), baselineY);
            path.Close();
        }

        for (var i = 1; i < values.Count; i++)
        {
            if (samples[i].SampledUtc - samples[i - 1].SampledUtc <= gapThreshold) continue;

            CloseRun(i - 1);
            runStart = i;
        }

        CloseRun(values.Count - 1);
        return path;
    }

    private static DateTime LocalMidnightToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// Rounds up to a readable axis ceiling so gridlines land on whole numbers.
    /// Same logic as <see cref="MemberActivityChartRenderer"/>.
    /// </summary>
    private static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        if (value <= 5) return 5;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;

        double nice;
        if      (normalized <= 1)   nice = 1;
        else if (normalized <= 2)   nice = 2;
        else if (normalized <= 2.5) nice = 2.5;
        else if (normalized <= 5)   nice = 5;
        else                        nice = 10;

        return nice * magnitude;
    }

    private static string FormatHours(double hours) =>
        hours == Math.Floor(hours) ? $"{(int)hours}h" : $"{hours:0.#}h";
}
