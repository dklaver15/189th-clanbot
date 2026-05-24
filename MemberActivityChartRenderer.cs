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

    // ── Discord-themed palette ──────────────────────────────────────────
    // Matches the bot's Discord client's dark-mode palette so the chart
    // sits naturally inside an embed. Hex strings parsed at use site to
    // avoid the ScottPlot.Color/Discord.Color name collision at the
    // namespace level.
    private const string FigureBgHex   = "#2b2d31";   // chart frame
    private const string DataBgHex     = "#1e1f22";   // plot area
    private const string AxisColorHex  = "#b5bac1";   // axis labels & ticks
    private const string GridColorHex  = "#404249";   // major gridlines
    private const string BarColorHex   = "#5865f2";   // Discord blurple
    private const string LineColorHex  = "#c9a647";   // 189th gold

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
            var plot = new ScottPlot.Plot();

            // Themed background, axis, and grid colors.
            plot.FigureBackground.Color = ScottPlot.Color.FromHex(FigureBgHex);
            plot.DataBackground.Color   = ScottPlot.Color.FromHex(DataBgHex);
            plot.Axes.Color(ScottPlot.Color.FromHex(AxisColorHex));
            plot.Grid.MajorLineColor    = ScottPlot.Color.FromHex(GridColorHex);

            // Bars: one per day. Position is the day's OADate (1 unit per
            // day on the ScottPlot DateTime axis), so the default bar width
            // of 1 unit makes adjacent days touch. That's the right look
            // for a daily-activity bar chart — gaps between bars would
            // suggest missing data rather than zero activity.
            var barColor = ScottPlot.Color.FromHex(BarColorHex);
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

            // Rolling-average line. ScatterLine() is the documented v5
            // idiom for "line with no markers" — it returns the same
            // Scatter plottable but with MarkerSize already set to 0,
            // saving the explicit set and avoiding the v4→v5 enum
            // rename ambiguity around marker styles.
            var lineXs = orderedDays.Select(d => d.ToOADate()).ToArray();
            var lineColor = ScottPlot.Color.FromHex(LineColorHex);
            var rollingLine = plot.Add.ScatterLine(lineXs, rolling);
            rollingLine.Color      = lineColor;
            rollingLine.LineWidth  = 2.5f;
            rollingLine.LegendText = $"{RollingWindowDays}-day average";

            // DateTime axis + autoscale.
            plot.Axes.DateTimeTicksBottom();
            plot.Axes.Margins(bottom: 0);  // bars sit flush on the x-axis
            plot.YLabel("Messages per day");
            plot.Title($"Daily activity — {displayName} (last {WindowDays} days)");

            // Default legend placement (upper-right). Style it to match
            // the dark theme so it doesn't punch through as a white box.
            plot.ShowLegend();
            plot.Legend.BackgroundColor = ScottPlot.Color.FromHex(DataBgHex);
            plot.Legend.FontColor       = ScottPlot.Color.FromHex(AxisColorHex);
            plot.Legend.OutlineColor    = ScottPlot.Color.FromHex(GridColorHex);

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
