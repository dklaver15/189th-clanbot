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
/// Handles the /calendar slash command — a day-by-day view of upcoming
/// events on the clan's Google Calendar, rendered as an ephemeral embed.
///
/// ── Why this exists ──
/// Members regularly want to know "what's on the schedule this week?"
/// without leaving Discord, opening a browser, and signing into Google
/// Calendar. Apollo posts give the upcoming-event view per-event in the
/// #events channel, but there's no compact "all of next week at a glance"
/// option. This is that.
///
/// ── Source of truth ──
/// Queries Google Calendar directly via GoogleCalendarService.ListAllEvents-
/// Async rather than reading from the local CalendarEvents table. Reasoning:
///   • Anyone with edit access to the calendar (officers fixing typos,
///     rescheduling via the GCal UI) can change times/titles without going
///     through the bot. The local table doesn't reflect those edits.
///   • Phase 4's reconciler will eventually catch DB-vs-GCal drift, but
///     for a read-only "what's actually scheduled" command, GCal is the
///     authoritative answer.
/// Trade-off: an outage of the Google API makes this command return an
/// error to the user. Acceptable — it's a convenience read; if Google is
/// down the user can hit refresh in a minute or open the calendar tab.
///
/// ── Date grouping vs time display ──
/// Two different audiences for the timestamps in this embed:
///   1. WHICH DAY an event is on — needs a fixed reference TZ so all
///      readers see the same grouping. Picked America/Chicago because
///      the 189th plays evening Central time and most members are in US
///      time zones; grouping in UTC would split a single ops night across
///      two date headers (since 8pm CT = 1am UTC the next day).
///   2. THE ACTUAL TIME the event starts/ends — needs to render in each
///      reader's local zone. Discord's &lt;t:unix:t&gt; markdown does this
///      automatically, so we use that for the time-of-day portion and
///      let Discord handle TZ conversion per viewer.
/// The footer makes the dual-zone behavior explicit.
///
/// ── Empty days ──
/// Skipped entirely rather than shown as empty fields. A 7-day window with
/// events on only 2 days is rendered as 2 fields, not 7. Reduces noise and
/// avoids burning embed-field budget on padding.
///
/// ── Ephemeral ──
/// Always ephemeral. /calendar is informational and tends to be invoked
/// repeatedly (e.g. someone asks "what's tonight?" in chat); broadcasting
/// the full embed each time would clutter the channel.
///
/// ── Permissions ──
/// No rank gate. Schedule visibility benefits everyone in the clan,
/// including recruits who want to know what events are coming up.
/// </summary>
public class CalendarCommandHandler
{
    private const string CommandName = "calendar";

    /// <summary>
    /// IANA time zone used for grouping events under date headers. Hard-
    /// coded to America/Chicago because the 189th plays evening Central
    /// and most members are US-based; grouping in UTC would split a
    /// single ops night across two date headers. Move to BotConfig if the
    /// clan ever needs per-deployment configurability.
    /// </summary>
    private const string DisplayTimeZoneId = "America/Chicago";

    /// <summary>Default look-ahead window when /calendar is invoked without arguments.</summary>
    private const int DefaultDays = 7;

    /// <summary>Hard cap on the days option. 30 keeps the embed inside Discord limits even on a busy month.</summary>
    private const int MaxDays = 30;

    /// <summary>Minimum days option value. 1 = "just today plus the rollover into tomorrow."</summary>
    private const int MinDays = 1;

    /// <summary>Discord embed field-value char cap. Used to truncate per-day event lists defensively.</summary>
    private const int DiscordFieldValueLimit = 1024;

    /// <summary>Discord embed total char cap across all fields + title + description + footer.</summary>
    private const int DiscordEmbedTotalLimit = 6000;

    /// <summary>Discord embed field-count cap. One field per day = up to 30; we cap at 25 just in case.</summary>
    private const int DiscordEmbedMaxFields = 25;

    /// <summary>Headroom held back from the embed limits for the "more events not shown" notice.</summary>
    private const int TruncationSummaryReserve = 200;

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

        // Window: from now (UTC) to now + days days. Past events are
        // intentionally excluded — /calendar is "what's coming up", not
        // "what just happened" (that's /attendance).
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

        if (events.Count == 0)
        {
            await command.FollowupAsync(
                embed: BuildEmptyEmbed(days),
                ephemeral: true);
            return;
        }

        var displayTz = ResolveDisplayTimeZone();
        var embed = BuildCalendarEmbed(events, days, displayTz);

        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    // ─── Embed Construction ───────────────────────────────────────────

    private static Embed BuildEmptyEmbed(int days)
    {
        var span = days == 1 ? "the next 24 hours"
                 : days == 7 ? "the next week"
                 : $"the next {days} days";

        return new EmbedBuilder()
            .WithTitle("📅 189th Calendar")
            .WithDescription($"No events scheduled in {span}.")
            .WithColor(new Color(60, 120, 90))
            .Build();
    }

    private static Embed BuildCalendarEmbed(
        IReadOnlyList<Event> events,
        int days,
        TimeZoneInfo displayTz)
    {
        // Resolve "today" / "tomorrow" against the display TZ so the friendly
        // labels match how readers think about days. Mismatch with the
        // viewer's actual local TZ is acceptable: the footer makes the
        // grouping zone explicit, and per-event time markdown still
        // renders in the viewer's local zone.
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, displayTz);
        var todayLocal = DateOnly.FromDateTime(nowLocal);
        var tomorrowLocal = todayLocal.AddDays(1);

        // Group by display-TZ date, ordered chronologically.
        var grouped = events
            .Select(e => (Event: e, LocalDate: ResolveLocalDate(e, displayTz)))
            .Where(x => x.LocalDate is not null)
            .GroupBy(x => x.LocalDate!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        var span = days == 1 ? "next 24 hours"
                 : days == 7 ? "next 7 days"
                 : $"next {days} days";

        var builder = new EmbedBuilder()
            .WithTitle("📅 189th Calendar")
            .WithDescription($"Events for the {span} · {grouped.Sum(g => g.Count())} total")
            .WithColor(new Color(60, 120, 90))
            .WithFooter("Times shown in your local zone · Day grouping in Central Time");

        // Track the next upcoming event so we can decorate it with a
        // relative-time marker ("in 3 hours"). Most useful on the very
        // next thing happening; redundant on later events where the date
        // header already conveys "this is X days from now."
        var nextEvent = events
            .Select(e => (Event: e, StartUtc: GoogleCalendarService.ParseEventTime(e.Start)))
            .Where(x => x.StartUtc is not null && x.StartUtc.Value > DateTime.UtcNow)
            .OrderBy(x => x.StartUtc)
            .FirstOrDefault();

        var nextEventId = nextEvent.Event?.Id;

        // Track running totals so we stop adding fields before Discord
        // rejects the whole embed. Same defensive pattern as
        // AttendanceCommandHandler.
        var totalChars = (builder.Title?.Length ?? 0)
                       + (builder.Description?.Length ?? 0)
                       + (builder.Footer?.Text?.Length ?? 0);
        var fieldsRemaining = DiscordEmbedMaxFields;
        var truncated = false;

        foreach (var dayGroup in grouped)
        {
            if (fieldsRemaining <= 1
                || totalChars >= DiscordEmbedTotalLimit - TruncationSummaryReserve)
            {
                truncated = true;
                break;
            }

            var fieldName  = BuildDayHeader(dayGroup.Key, todayLocal, tomorrowLocal);
            var fieldValue = BuildDayValue(dayGroup.Select(x => x.Event), nextEventId);

            // Defensive truncation: if a single day's events somehow exceed
            // the per-field limit (very unlikely for clan ops but possible
            // for an unusually busy day), trim with a notice. The notice
            // text reserve (256 chars) lets us always fit the trailing line.
            if (fieldValue.Length > DiscordFieldValueLimit)
            {
                const int reserve = 256;
                fieldValue = fieldValue[..(DiscordFieldValueLimit - reserve)]
                           + "\n_…more events on this day not shown_";
                truncated = true;
            }

            // Skip this field if adding it would push us past the total.
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

    private static string BuildDayHeader(DateOnly day, DateOnly today, DateOnly tomorrow)
    {
        var label = day == today    ? "**Today**"
                  : day == tomorrow ? "**Tomorrow**"
                  : null;

        // Always include the actual date so readers can disambiguate
        // "Today" against the grouping zone if their local zone disagrees.
        var datePart = day.ToString("dddd, MMM d");

        return label is null ? datePart : $"{label} · {datePart}";
    }

    private static string BuildDayValue(IEnumerable<Event> dayEvents, string? nextEventId)
    {
        var sb = new StringBuilder();

        foreach (var e in dayEvents)
        {
            var startUtc = GoogleCalendarService.ParseEventTime(e.Start);
            var endUtc   = GoogleCalendarService.ParseEventTime(e.End);
            if (startUtc is null) continue;

            // All-day events have a Date but no DateTime on Start. The
            // GCal client surfaces this as an empty DateTimeRaw.
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

                // Relative-time hint on the very next upcoming event only.
                // Discord renders <t:R> as "in 3 hours" / "in 2 days" etc.
                if (e.Id == nextEventId)
                {
                    sb.Append(" · <t:").Append(startUnix).Append(":R>");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static int ResolveDaysOption(SocketSlashCommand command)
    {
        var raw = command.Data.Options.FirstOrDefault(o => o.Name == "days")?.Value;
        if (raw is long longVal)
        {
            var i = (int)Math.Clamp(longVal, MinDays, MaxDays);
            return i;
        }
        return DefaultDays;
    }

    private TimeZoneInfo ResolveDisplayTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DisplayTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Container missing tzdata or unexpected TZ name. Fall back
            // to UTC and log; the embed still renders, headers will be
            // grouped by UTC date instead of CT date.
            _logger.LogWarning(
                "Time zone '{Tz}' not found; falling back to UTC for day grouping",
                DisplayTimeZoneId);
            return TimeZoneInfo.Utc;
        }
    }

    private static DateOnly? ResolveLocalDate(Event e, TimeZoneInfo tz)
    {
        var startUtc = GoogleCalendarService.ParseEventTime(e.Start);
        if (startUtc is null) return null;

        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc.Value, tz);
        return DateOnly.FromDateTime(startLocal);
    }

    /// <summary>
    /// Escapes Discord markdown special characters in a string used inside
    /// embed text. Apollo titles occasionally contain underscores or
    /// asterisks (e.g. "Op *Vendetta*") that would otherwise be interpreted
    /// as italic/bold markers and visually break the field.
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
