using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /calendar slash command — a view of upcoming events on the
/// clan's Google Calendar, rendered as an ephemeral embed.
///
/// Two render modes selectable via the `view` option:
///   • list (default) — date-grouped fields, each event a bullet line with
///     viewer-local time markdown. Polished with empty-day placeholders for
///     ranges ≤ 7 days, and week-boundary dividers for longer ranges.
///   • grid — top-of-embed monospace week strip (Mon–Sun, today bracketed,
///     event counts per cell), followed by per-day prose details below.
///     The grid is necessarily in Central Time (Discord's &lt;t:UNIX:t&gt;
///     markdown does not render inside code blocks); the per-event detail
///     lines below the grid still use the viewer-local time markdown.
///
/// ── Source of truth ──
/// Queries Google Calendar directly via GoogleCalendarService.ListAllEvents-
/// Async rather than the local CalendarEvents table. Reasoning: anyone with
/// edit access to GCal can change times/titles via the calendar UI without
/// going through the bot, and the local table doesn't reflect those edits.
/// /calendar is "what's actually scheduled," so GCal wins. Phase 4's
/// reconciler is what catches DB↔GCal drift; this command intentionally
/// doesn't try.
///
/// ── Date grouping vs time display ──
/// Two different audiences for timestamps in this embed:
///   1. WHICH DAY an event is on — needs a fixed reference TZ so all readers
///      see the same grouping. Picked America/Chicago because the 189th
///      plays evening Central time and most members are US-based; grouping
///      in UTC would split a single ops night across two date headers.
///   2. THE ACTUAL TIME the event starts/ends (in list mode and in grid-mode
///      details) — &lt;t:unix:t&gt; markdown auto-converts to viewer-local. Inside
///      the grid code block, times are forced to CT because Discord doesn't
///      render the markdown there.
/// Footer makes the dual-zone behavior explicit.
///
/// ── Permissions ──
/// No rank gate. Schedule visibility benefits everyone in the clan,
/// including recruits.
///
/// ── Ephemeral ──
/// Always ephemeral. /calendar is informational and tends to be invoked
/// repeatedly; broadcasting the full embed each time would clutter
/// channels.
/// </summary>
public class CalendarCommandHandler
{
    private const string CommandName = "calendar";

    /// <summary>
    /// IANA time zone used for grouping events under date headers and for
    /// rendering times inside the grid code block. Hard-coded to
    /// America/Chicago because the 189th plays evening Central and most
    /// members are US-based; grouping in UTC would split a single ops
    /// night across two date headers. Move to BotConfig if the clan ever
    /// needs per-deployment configurability.
    /// </summary>
    private const string DisplayTimeZoneId = "America/Chicago";

    /// <summary>Default look-ahead window when /calendar is invoked without arguments.</summary>
    private const int DefaultDays = 7;

    /// <summary>Hard cap on the days option for list mode.</summary>
    private const int MaxDays = 30;

    /// <summary>Cap for grid mode. Two-week max keeps the grid header readable on mobile.</summary>
    private const int MaxGridDays = 14;

    /// <summary>Minimum days option value. 1 = "just today plus the rollover into tomorrow."</summary>
    private const int MinDays = 1;

    /// <summary>Threshold below which list mode shows every day in the range, including empty ones.</summary>
    private const int CompactListDayThreshold = 7;

    private const string ViewList = "list";
    private const string ViewGrid = "grid";
    private const string DefaultView = ViewList;

    /// <summary>Discord embed field-value char cap. Used to truncate per-day event lists defensively.</summary>
    private const int DiscordFieldValueLimit = 1024;

    /// <summary>Discord embed total char cap across all fields + title + description + footer.</summary>
    private const int DiscordEmbedTotalLimit = 6000;

    /// <summary>Discord embed field-count cap. We keep one slot in reserve for the truncation notice.</summary>
    private const int DiscordEmbedMaxFields = 25;

    /// <summary>Headroom held back from the embed limits for the truncation notice and other tail content.</summary>
    private const int TruncationSummaryReserve = 200;

    /// <summary>Width of each day column inside the grid code block. 5 chars fits "[Wed]" or " Wed " or "  13 ".</summary>
    private const int GridCellWidth = 5;

    private readonly GoogleCalendarService _calendar;
    private readonly ILogger<CalendarCommandHandler> _logger;
    private readonly BotConfig _config;

    public CalendarCommandHandler(
        GoogleCalendarService calendar,
        ILogger<CalendarCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _calendar = calendar;
        _logger   = logger;
        _config   = config.Value;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName)
            return;

        try
        {
            await HandleCalendarAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try
            {
                await command.FollowupAsync(
                    "Something went wrong rendering the calendar. Check the bot logs.",
                    ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleCalendarAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var days = ResolveDaysOption(command);
        var view = ResolveViewOption(command);

        // Grid mode rounds up to whole weeks (cleaner header) and caps at
        // two weeks regardless of the user-supplied value. Two rows of seven
        // is the practical readable limit on mobile.
        if (view == ViewGrid)
        {
            days = Math.Min(RoundUpToNearestWeek(days), MaxGridDays);
        }

        var nowUtc  = DateTime.UtcNow;
        var fromUtc = nowUtc;
        var toUtc   = nowUtc.AddDays(days);

        List<Event> events;
        try
        {
            events = await _calendar.ListAllEventsAsync(fromUtc, toUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list calendar events for /calendar");
            await command.FollowupAsync(
                "Couldn't reach Google Calendar. Try again in a moment.",
                ephemeral: true);
            return;
        }

        var displayTz = ResolveDisplayTimeZone();

        if (events.Count == 0)
        {
            await command.FollowupAsync(
                embed: BuildEmptyEmbed(days, view),
                ephemeral: true);
            return;
        }

        Embed embed = view == ViewGrid
            ? BuildGridEmbed(events, days, displayTz)
            : BuildListEmbed(events, days, displayTz);

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    // ─── List-View Embed ──────────────────────────────────────────────

    private static Embed BuildListEmbed(
        IReadOnlyList<Event> events,
        int days,
        TimeZoneInfo displayTz)
    {
        var nowLocal      = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, displayTz);
        var todayLocal    = DateOnly.FromDateTime(nowLocal);
        var tomorrowLocal = todayLocal.AddDays(1);

        var grouped = GroupEventsByLocalDate(events, displayTz);

        var span = days == 1 ? "next 24 hours"
                 : days == 7 ? "next 7 days"
                 : $"next {days} days";

        var builder = new EmbedBuilder()
            .WithTitle("📅 189th Calendar")
            .WithDescription($"Events for the {span} · {grouped.Values.Sum(v => v.Count)} total")
            .WithColor(new Color(60, 120, 90))
            .WithFooter("Times shown in your local zone · Day grouping in Central Time");

        var nextEventId = ResolveNextEventId(events);

        // Day enumeration strategy depends on range size:
        //   • Compact (≤ 7 days): show every day, render empty placeholders
        //     so the rhythm of the week is visible.
        //   • Wide (> 7 days): show only event-bearing days, but insert a
        //     "── Week of MMM dd ───" divider before any event-day that
        //     starts a new calendar week. Skipping empties keeps us inside
        //     the field-count budget; dividers help readers reorient when
        //     gaps are large.
        var compact = days <= CompactListDayThreshold;

        var daysToRender = compact
            ? Enumerable.Range(0, days).Select(i => todayLocal.AddDays(i)).ToList()
            : grouped.Keys.OrderBy(d => d).ToList();

        // Running totals so we stop adding fields before Discord rejects
        // the whole embed. Same defensive pattern as AttendanceCommandHandler.
        var totalChars = (builder.Title?.Length ?? 0)
                       + (builder.Description?.Length ?? 0)
                       + (builder.Footer?.Text?.Length ?? 0);
        var fieldsRemaining = DiscordEmbedMaxFields;
        var truncated       = false;

        DateOnly? lastWeekStart = null;

        foreach (var day in daysToRender)
        {
            // Insert week-boundary divider in wide mode when this day starts
            // a different calendar week than the previous rendered day.
            if (!compact)
            {
                var weekStart = StartOfWeek(day);
                if (weekStart != lastWeekStart)
                {
                    if (fieldsRemaining <= 2
                        || totalChars >= DiscordEmbedTotalLimit - TruncationSummaryReserve)
                    {
                        truncated = true;
                        break;
                    }

                    var dividerName = $"── Week of {weekStart:MMM d} ───";
                    builder.AddField(dividerName, "\u200B");
                    totalChars += dividerName.Length + 1;
                    fieldsRemaining--;
                    lastWeekStart = weekStart;
                }
            }

            if (fieldsRemaining <= 1
                || totalChars >= DiscordEmbedTotalLimit - TruncationSummaryReserve)
            {
                truncated = true;
                break;
            }

            var hasEvents = grouped.TryGetValue(day, out var dayEvents) && dayEvents.Count > 0;

            var fieldName = BuildDayHeader(day, todayLocal, tomorrowLocal);
            var fieldValue = hasEvents
                ? BuildDayEventList(dayEvents!, nextEventId)
                : "_(no events)_";

            // Defensive truncation per-field. Apollo posts produce short
            // titles; comp events can occasionally have longer ones.
            if (fieldValue.Length > DiscordFieldValueLimit)
            {
                const int reserve = 256;
                fieldValue = fieldValue[..(DiscordFieldValueLimit - reserve)]
                           + "\n_…more events on this day not shown_";
                truncated = true;
            }

            var fieldCost = fieldName.Length + fieldValue.Length;
            if (totalChars + fieldCost > DiscordEmbedTotalLimit - TruncationSummaryReserve)
            {
                truncated = true;
                break;
            }

            builder.AddField(fieldName, fieldValue);
            totalChars += fieldCost;
            fieldsRemaining--;
        }

        if (truncated)
        {
            builder.WithDescription(
                $"{builder.Description}\n\n_Output truncated — open Google Calendar for the full view._");
        }

        return builder.Build();
    }

    // ─── Grid-View Embed ──────────────────────────────────────────────

    private static Embed BuildGridEmbed(
        IReadOnlyList<Event> events,
        int days,
        TimeZoneInfo displayTz)
    {
        var nowLocal      = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, displayTz);
        var todayLocal    = DateOnly.FromDateTime(nowLocal);
        var tomorrowLocal = todayLocal.AddDays(1);

        var allDays = Enumerable.Range(0, days)
            .Select(i => todayLocal.AddDays(i))
            .ToList();

        var grouped     = GroupEventsByLocalDate(events, displayTz);
        var nextEventId = ResolveNextEventId(events);

        var grid = BuildGridBlock(allDays, grouped, todayLocal);

        var weeks      = days / 7;
        var weekLabel  = weeks == 1 ? "next 7 days" : $"next {weeks} weeks";
        var totalCount = grouped.Values.Sum(v => v.Count);

        var description = $"Grid view · {weekLabel} · {totalCount} total\n{grid}";

        var builder = new EmbedBuilder()
            .WithTitle("📅 189th Calendar")
            .WithDescription(description)
            .WithColor(new Color(60, 120, 90))
            .WithFooter("Grid times in Central · per-event times below in your local zone");

        var fieldsRemaining = DiscordEmbedMaxFields;
        var totalChars      = (builder.Title?.Length ?? 0)
                            + (builder.Description?.Length ?? 0)
                            + (builder.Footer?.Text?.Length ?? 0);
        var truncated       = false;

        // Per-day prose details below the grid. Empty days don't get fields
        // here — the grid above already shows their dot, so the field would
        // be redundant noise. This also keeps us inside the 25-field cap
        // for two-week views with many event days.
        foreach (var day in allDays)
        {
            if (!grouped.TryGetValue(day, out var dayEvents) || dayEvents.Count == 0)
                continue;

            if (fieldsRemaining <= 1
                || totalChars >= DiscordEmbedTotalLimit - TruncationSummaryReserve)
            {
                truncated = true;
                break;
            }

            var fieldName  = BuildDayHeader(day, todayLocal, tomorrowLocal);
            var fieldValue = BuildDayEventList(dayEvents, nextEventId);

            if (fieldValue.Length > DiscordFieldValueLimit)
            {
                const int reserve = 256;
                fieldValue = fieldValue[..(DiscordFieldValueLimit - reserve)]
                           + "\n_…more events on this day not shown_";
                truncated = true;
            }

            var fieldCost = fieldName.Length + fieldValue.Length;
            if (totalChars + fieldCost > DiscordEmbedTotalLimit - TruncationSummaryReserve)
            {
                truncated = true;
                break;
            }

            builder.AddField(fieldName, fieldValue);
            totalChars += fieldCost;
            fieldsRemaining--;
        }

        if (truncated)
        {
            builder.WithDescription(description + "\n_Output truncated — open Google Calendar for the full view._");
        }

        return builder.Build();
    }

    /// <summary>
    /// Builds the monospace week strip(s) shown at the top of grid mode.
    /// Three rows per week: day-of-week (today bracketed), day-of-month,
    /// event count (or · for zero). Inside ``` fences so Discord renders
    /// it monospace.
    /// </summary>
    private static string BuildGridBlock(
        IReadOnlyList<DateOnly> allDays,
        IReadOnlyDictionary<DateOnly, List<Event>> grouped,
        DateOnly today)
    {
        var sb = new StringBuilder();
        sb.Append("```");

        var weekCount = (allDays.Count + 6) / 7;

        for (var w = 0; w < weekCount; w++)
        {
            var weekDays = allDays.Skip(w * 7).Take(7).ToList();

            sb.Append('\n');

            // Row 1 — day-of-week labels (today bracketed).
            foreach (var d in weekDays)
            {
                var label = d == today
                    ? $"[{d:ddd}]"
                    : Center(d.ToString("ddd"), GridCellWidth);
                sb.Append(label);
            }
            sb.Append('\n');

            // Row 2 — day-of-month numbers.
            foreach (var d in weekDays)
            {
                sb.Append(Center(d.Day.ToString(), GridCellWidth));
            }
            sb.Append('\n');

            // Row 3 — event counts.
            foreach (var d in weekDays)
            {
                var count = grouped.TryGetValue(d, out var ev) ? ev.Count : 0;
                var content = count == 0 ? "·" : count.ToString();
                sb.Append(Center(content, GridCellWidth));
            }

            // Blank line between weeks for readability.
            if (w + 1 < weekCount) sb.Append('\n');
        }

        sb.Append("\n```");
        return sb.ToString();
    }

    // ─── Day-Field Builders (shared by both views) ────────────────────

    private static string BuildDayHeader(DateOnly day, DateOnly today, DateOnly tomorrow)
    {
        var label = day == today    ? "**Today**"
                  : day == tomorrow ? "**Tomorrow**"
                  : null;

        var datePart = day.ToString("dddd, MMM d");
        return label is null ? datePart : $"{label} · {datePart}";
    }

    private static string BuildDayEventList(IEnumerable<Event> dayEvents, string? nextEventId)
    {
        var sb = new StringBuilder();

        foreach (var e in dayEvents)
        {
            var startUtc = GoogleCalendarService.ParseEventTime(e.Start);
            var endUtc   = GoogleCalendarService.ParseEventTime(e.End);
            if (startUtc is null) continue;

            var isAllDay = string.IsNullOrEmpty(e.Start.DateTimeRaw);

            var title = string.IsNullOrWhiteSpace(e.Summary) ? "(untitled)" : e.Summary.Trim();
            var titleMarkdown = string.IsNullOrEmpty(e.HtmlLink)
                ? $"**{EscapeMarkdown(title)}**"
                : $"**[{EscapeMarkdown(title)}]({e.HtmlLink})**";

            sb.Append("• ");

            if (isAllDay)
            {
                sb.Append("All day · ").Append(titleMarkdown);
            }
            else
            {
                var startUnix = new DateTimeOffset(startUtc.Value).ToUnixTimeSeconds();
                sb.Append("<t:").Append(startUnix).Append(":t>");

                if (endUtc is not null)
                {
                    var endUnix = new DateTimeOffset(endUtc.Value).ToUnixTimeSeconds();
                    sb.Append(" – <t:").Append(endUnix).Append(":t>");
                }

                sb.Append(" · ").Append(titleMarkdown);

                if (e.Id == nextEventId)
                {
                    sb.Append(" · <t:").Append(startUnix).Append(":R>");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ─── Empty-State Embed ────────────────────────────────────────────

    private static Embed BuildEmptyEmbed(int days, string view)
    {
        var span = days == 1 ? "the next 24 hours"
                 : days == 7 ? "the next week"
                 : $"the next {days} days";

        var note = view == ViewGrid
            ? "\n\n_Grid view rounds up to whole weeks (1–2 weeks shown)._"
            : string.Empty;

        return new EmbedBuilder()
            .WithTitle("📅 189th Calendar")
            .WithDescription($"No events scheduled in {span}.{note}")
            .WithColor(new Color(60, 120, 90))
            .Build();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static int ResolveDaysOption(SocketSlashCommand command)
    {
        var raw = command.Data.Options.FirstOrDefault(o => o.Name == "days")?.Value;
        if (raw is long longVal)
            return (int)Math.Clamp(longVal, MinDays, MaxDays);
        return DefaultDays;
    }

    private static string ResolveViewOption(SocketSlashCommand command)
    {
        var raw = command.Data.Options.FirstOrDefault(o => o.Name == "view")?.Value as string;
        return raw switch
        {
            ViewGrid => ViewGrid,
            ViewList => ViewList,
            _        => DefaultView,
        };
    }

    private TimeZoneInfo ResolveDisplayTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DisplayTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning(
                "Time zone '{Tz}' not found; falling back to UTC for day grouping",
                DisplayTimeZoneId);
            return TimeZoneInfo.Utc;
        }
    }

    private static Dictionary<DateOnly, List<Event>> GroupEventsByLocalDate(
        IReadOnlyList<Event> events,
        TimeZoneInfo tz)
    {
        return events
            .Select(e => (Event: e, LocalDate: ResolveLocalDate(e, tz)))
            .Where(x => x.LocalDate is not null)
            .GroupBy(x => x.LocalDate!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Event).ToList());
    }

    private static DateOnly? ResolveLocalDate(Event e, TimeZoneInfo tz)
    {
        var startUtc = GoogleCalendarService.ParseEventTime(e.Start);
        if (startUtc is null) return null;

        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc.Value, tz);
        return DateOnly.FromDateTime(startLocal);
    }

    private static string? ResolveNextEventId(IReadOnlyList<Event> events)
    {
        return events
            .Select(e => (Event: e, StartUtc: GoogleCalendarService.ParseEventTime(e.Start)))
            .Where(x => x.StartUtc is not null && x.StartUtc.Value > DateTime.UtcNow)
            .OrderBy(x => x.StartUtc)
            .FirstOrDefault().Event?.Id;
    }

    /// <summary>
    /// Returns the Sunday on or before the given date. US convention —
    /// matches how 189th members think about week boundaries when looking
    /// at a calendar.
    /// </summary>
    private static DateOnly StartOfWeek(DateOnly day)
    {
        var diff = (int)day.DayOfWeek; // Sunday = 0
        return day.AddDays(-diff);
    }

    private static int RoundUpToNearestWeek(int days) => ((days + 6) / 7) * 7;

    /// <summary>
    /// Centers a string within a fixed-width cell, padding with spaces.
    /// Truncates if content exceeds width (shouldn't happen for our content
    /// — day-of-week is always 3 chars, day-of-month is 1–2, count usually
    /// 1; defensive only).
    /// </summary>
    private static string Center(string content, int width)
    {
        if (content.Length >= width) return content[..width];
        var pad   = width - content.Length;
        var left  = pad / 2;
        var right = pad - left;
        return new string(' ', left) + content + new string(' ', right);
    }

    /// <summary>
    /// Escapes Discord markdown special characters in event titles.
    /// Apollo titles occasionally contain underscores or asterisks that
    /// would otherwise be interpreted as italic/bold markers.
    /// </summary>
    private static string EscapeMarkdown(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (c is '*' or '_' or '`' or '~' or '|' or '\\' or '[' or ']')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}