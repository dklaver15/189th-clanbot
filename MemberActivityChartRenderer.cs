using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// ── Failure handling ──
/// Every public method is wrapped in try/catch and returns null on
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
/// foreign element. The 189th-gold accent picks up the same colour the
/// rest of the bot uses on auto-promotion announcements.
///
/// ── Output dimensions ──
/// Default 900×300 — wide enough to fit ~90 daily bars with breathing
/// room, short enough that the chart doesn't dominate the embed on
/// mobile. Discord renders attached PNGs at native pixel size up to
/// roughly 600px wide on the standard message column, so we generate
/// at 2× width for retina sharpness.
/// </summary>
public class MemberActivityChartRenderer
{
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<MemberActivityChartRenderer> _logger;

    // ── Sci-fi-HUD palette ──────────────────────────────────────────────
    // The previous theme had FigureBg slightly lighter than DataBg, which
    // produced a visible inset rectangle around the data even after the
    // frame line was hidden — the eye reads two-tone backgrounds as a box.
    // Here both the figure and data backgrounds use the same deep
    // near-black so the chart edge-blends seamlessly into the Discord
    // embed. The trend line picks up a brighter gold + a glow underlayer
    // for the HUD feel. Hex values verified against Discord's dark-theme
    // palette + 189th brand gold.
    private const string BackgroundHex = "#0d1017";   // unified figure + data bg (deep console black)
    private const string AxisColorHex  = "#ffffff";   // axis labels & ticks (white for max legibility)
    private const string GridColorHex  = "#ffd86b";   // grid lines (gold-tinted, used at very low alpha)
    private const string BarColorHex   = "#5865f2";   // Discord blurple (bars)
    private const string LineColorHex  = "#ffd86b";   // brighter "command-deck" gold (was #c9a647)

    /// <summary>How many days of history the chart spans.</summary>
    private const int WindowDays = 90;

    /// <summary>
    /// Width of the trailing rolling average, in days. 7 is the standard
    /// "weekly engagement trend" window; smoother than daily counts but
    /// still responsive enough to show real changes in engagement.
    /// </summary>
    private const int RollingWindowDays = 7;

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
            var totalMessages = timestamps.Count;
            if (totalMessages == 0)
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

            // ── Build the plot ──────────────────────────────────────────
            // Fully-qualified ScottPlot namespace at every use site so the
            // ambient ScottPlot.Color type doesn't collide with
            // Discord.Color elsewhere in the project.
            //
            // Visual direction: "modern dashboard" — Discord-dark background,
            // semi-transparent bars in the back, a glowing smooth trend line
            // in front with a soft area fill, and a horizontal-only grid.
            // Every styling change here is anchored to a specific ScottPlot 5
            // cookbook recipe; see the search-history comments inline.
            var plot = new ScottPlot.Plot();

            // Themed backgrounds. Both Figure and Data backgrounds use the
            // SAME deep near-black so there's no two-tone inset rectangle
            // around the data — the chart edge-blends seamlessly into the
            // Discord embed.
            plot.FigureBackground.Color = ScottPlot.Color.FromHex(BackgroundHex);
            plot.DataBackground.Color   = ScottPlot.Color.FromHex(BackgroundHex);
            plot.Axes.Color(ScottPlot.Color.FromHex(AxisColorHex));

            // `Axes.Color(...)` above sets the axis frame line color and the
            // axis title color, but in ScottPlot 5 the tick label color is a
            // separate property on each axis's TickLabelStyle. Without these
            // explicit assignments the tick numbers/dates render in the
            // default light-gray, which is hard to read against the dark
            // embed background. Set them per axis to match the axis title
            // color (white). Bottom = X axis (dates), Left = Y axis (counts).
            var axisColor = ScottPlot.Color.FromHex(AxisColorHex);
            plot.Axes.Bottom.TickLabelStyle.ForeColor = axisColor;
            plot.Axes.Left.TickLabelStyle.ForeColor   = axisColor;

            // Hide the boxy chart frame (top/right/bottom/left axis lines)
            // so the data area edge-blends into the embed. Cookbook ref:
            // Styling/CustomBorders — `myPlot.Axes.Frame(false);`
            plot.Axes.Frame(false);

            // Horizontal-only grid lines, gold-tinted at very low alpha so
            // they read as faint HUD guides rather than structural elements.
            // Vertical grid lines are removed entirely. Cookbook ref:
            // CustomizingGrids and Styling/Grid recipes.
            plot.Grid.XAxisStyle.MajorLineStyle.Width = 0;
            plot.Grid.YAxisStyle.MajorLineStyle.Color =
                ScottPlot.Color.FromHex(GridColorHex).WithAlpha(0.10);
            plot.Grid.YAxisStyle.MajorLineStyle.Width = 1;

            // Bars: one per day at OADate positions (1 unit per day on the
            // DateTime axis), default width so adjacent days touch — gaps
            // between bars would suggest missing data rather than zero
            // activity. 75% alpha pulls them back from the foreground so the
            // gold trend line and its area fill can carry the visual weight.
            var barColor = ScottPlot.Color.FromHex(BarColorHex).WithAlpha(0.75);
            var bars = new List<ScottPlot.Bar>(orderedDays.Count);
            for (var i = 0; i < orderedDays.Count; i++)
            {
                bars.Add(new ScottPlot.Bar
                {
                    Position  = orderedDays[i].ToOADate(),
                    Value     = counts[i],
                    FillColor = barColor,
                    LineColor = barColor,  // no border outline against dark bg
                });
            }
            plot.Add.Bars(bars);

            // Rolling-average overlay rendered in TWO passes to produce a
            // HUD-style glow effect — the sci-fi "this thing is emitting
            // light" look that flat lines can't achieve in a single stroke:
            //
            //   PASS 1 (glow underlayer): wide, very transparent line at
            //     the same x,y data. The viewer reads this as the radiated
            //     halo around the trend rather than a separate line.
            //
            //   PASS 2 (sharp line): the actual data line at full opacity
            //     with the area fill beneath it. Rendered after the glow
            //     so it sits on top in z-order.
            //
            // Both passes use the QuadHalfPoint path strategy so the
            // smoothing is identical — otherwise the glow would peek out
            // from behind the sharp line on curves where they diverged.
            // Cookbook refs: Scatter/ScatterQuadHalfPath (smoothing) and
            // Scatter/ScatterFill (FillY area fill on the sharp line).
            var lineXs = orderedDays.Select(d => d.ToOADate()).ToArray();
            var lineColor = ScottPlot.Color.FromHex(LineColorHex);

            var trendGlow = plot.Add.ScatterLine(lineXs, rolling);
            trendGlow.Color        = lineColor.WithAlpha(0.20);
            trendGlow.LineWidth    = 11f;
            trendGlow.PathStrategy = new ScottPlot.PathStrategies.QuadHalfPoint();

            var rollingLine = plot.Add.ScatterLine(lineXs, rolling);
            rollingLine.Color        = lineColor;
            rollingLine.LineWidth    = 3f;
            rollingLine.PathStrategy = new ScottPlot.PathStrategies.QuadHalfPoint();
            rollingLine.FillY        = true;
            rollingLine.FillYColor   = lineColor.WithAlpha(0.18);

            // DateTime axis + autoscale. No Y-axis label — the title above
            // already carries the "messages per day" meaning, and removing
            // the rotated left-side label gives the chart more horizontal
            // breathing room.
            plot.Axes.DateTimeTicksBottom();
            plot.Axes.Margins(bottom: 0);  // bars sit flush on the x-axis
            plot.Title($"Daily activity — {displayName} (last {WindowDays} days)");

            // No legend either: there's only one labeled overlay (the trend
            // line) and its color + position next to the bars makes its
            // meaning self-evident. The legend was the last "boxy" UI
            // element competing with the data; removing it gives the HUD
            // aesthetic full canvas. (Default = legend hidden, so we just
            // don't call ShowLegend.)

            // Render at 2× the embed display width for retina sharpness.
            // ScottPlot returns PNG-encoded bytes directly — no temp file
            // needed. Signature is GetImageBytes(width, height, format).
            var png = plot.GetImageBytes(1800, 600, ScottPlot.ImageFormat.Png);
            return png;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to render activity chart for user {UserId} in guild {GuildId}",
                userId, guildId);
            return null;
        }
    }
}