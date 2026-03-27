using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /comp-event slash command, restricted to CPT and above.
///
/// Opens a 5-field modal for:
///   - Event Name
///   - Date (MM/DD/YYYY)
///   - Start Time (HH:MM UTC, 24-hour)
///   - End Time   (HH:MM UTC, 24-hour)
///   - Description (optional)
///
/// On submit, the event is:
///   1. Validated (date/time format, duration sanity check).
///   2. Checked for overlaps against all existing calendar events.
///   3. Created on Google Calendar with the [COMP] prefix and red/Tomato color.
///   4. Persisted to the CalendarEvents table.
///
/// Overlap conflicts are shown inline in the confirmation embed (not blocked) so
/// the officer making the event can see the conflict and coordinate manually.
/// </summary>
public class CompEventCommandHandler
{
    private readonly IServiceProvider _services;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<CompEventCommandHandler> _logger;
    private readonly BotConfig _config;

    public CompEventCommandHandler(
        IServiceProvider services,
        GoogleCalendarService calendarService,
        ILogger<CompEventCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services        = services;
        _calendarService = calendarService;
        _logger          = logger;
        _config          = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ModalSubmitted       += OnModalSubmittedAsync;
    }

    // ─── Slash Command ───────────────────────────────────────────────

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "comp-event") return;

        var guildUser = command.User as SocketGuildUser;
        if (guildUser is null || !HasCompEventPermission(guildUser))
        {
            await command.RespondAsync(
                $"❌ This command is restricted to **{_config.CompEventMinRank} and above**.",
                ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle("Create Comp Division Event")
            .WithCustomId("comp_event_modal")
            .AddTextInput(
                "Event Name", "event_name", TextInputStyle.Short,
                placeholder: "e.g. Comp Scrimmage vs. [Clan Name]",
                required: true, maxLength: 100)
            .AddTextInput(
                "Date (MM/DD/YYYY)", "event_date", TextInputStyle.Short,
                placeholder: "e.g. 04/15/2026",
                required: true, maxLength: 10)
            .AddTextInput(
                "Start Time (HH:MM UTC, 24-hour)", "event_start", TextInputStyle.Short,
                placeholder: "e.g. 20:00",
                required: true, maxLength: 5)
            .AddTextInput(
                "End Time (HH:MM UTC, 24-hour)", "event_end", TextInputStyle.Short,
                placeholder: "e.g. 22:00",
                required: true, maxLength: 5)
            .AddTextInput(
                "Description (optional)", "event_desc", TextInputStyle.Paragraph,
                placeholder: "Opponent info, map pool, roster notes...",
                required: false, maxLength: 500)
            .Build();

        await command.RespondWithModalAsync(modal);
    }

    // ─── Modal Submit ────────────────────────────────────────────────

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        if (modal.Data.CustomId != "comp_event_modal") return;

        await modal.DeferAsync(ephemeral: true);

        try
        {
            var fields = modal.Data.Components
                .ToDictionary(c => c.CustomId, c => c.Value?.Trim() ?? "");

            var eventName = fields.GetValueOrDefault("event_name", "");
            var dateStr   = fields.GetValueOrDefault("event_date", "");
            var startStr  = fields.GetValueOrDefault("event_start", "");
            var endStr    = fields.GetValueOrDefault("event_end",   "");
            var desc      = fields.GetValueOrDefault("event_desc",  "");

            // ── Validation ────────────────────────────────────────────
            if (!TryParseEventDateTime(dateStr, startStr, out var startUtc))
            {
                await modal.FollowupAsync(
                    "⚠️ **Invalid date or start time.**\n" +
                    "Use `MM/DD/YYYY` for the date and `HH:MM` (24-hour UTC) for the time.\n" +
                    "_Example: date `04/15/2026`, start `20:00`_",
                    ephemeral: true);
                return;
            }

            if (!TryParseEventDateTime(dateStr, endStr, out var endUtc))
            {
                await modal.FollowupAsync(
                    "⚠️ **Invalid end time.** Use `HH:MM` (24-hour UTC) format.\n" +
                    "_Example: `22:00`_",
                    ephemeral: true);
                return;
            }

            // Handle midnight crossover (end time before start = next day)
            if (endUtc <= startUtc)
                endUtc = endUtc.AddDays(1);

            if ((endUtc - startUtc).TotalHours > 24)
            {
                await modal.FollowupAsync(
                    "⚠️ Event duration cannot exceed 24 hours. Please check your start and end times.",
                    ephemeral: true);
                return;
            }

            var creatorName = modal.User.GlobalName ?? modal.User.Username;
            var calTitle    = $"[COMP] {eventName}";

            // ── Overlap Check ─────────────────────────────────────────
            var overlaps = await _calendarService.GetOverlappingEventsAsync(startUtc, endUtc);

            // ── Calendar Create ───────────────────────────────────────
            var calEvent = await _calendarService.CreateEventAsync(
                title:       calTitle,
                startUtc:    startUtc,
                endUtc:      endUtc,
                description: desc,
                creatorName: creatorName,
                source:      "CompDiv");

            // ── DB Persist ────────────────────────────────────────────
            if (modal.GuildId.HasValue)
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

                db.CalendarEvents.Add(new CalendarEvent
                {
                    GuildId          = modal.GuildId.Value,
                    DiscordMessageId = 0,          // no Discord message — comp events are calendar-only
                    CalendarEventId  = calEvent.Id,
                    Title            = calTitle,
                    StartUtc         = startUtc,
                    EndUtc           = endUtc,
                    Source           = "CompDiv",
                    CreatedAt        = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // ── Response Embed ────────────────────────────────────────
            var unixStart = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
            var unixEnd   = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

            var embedColor = overlaps.Count > 0 ? Color.Orange : Color.Red;

            var embedBuilder = new EmbedBuilder()
                .WithTitle("🏆 Comp Event Created!")
                .WithColor(embedColor)
                .AddField("Event",      eventName,                       true)
                .AddField("Created By", creatorName,                     true)
                .AddField("Start",      $"<t:{unixStart}:F>",           false)
                .AddField("End",        $"<t:{unixEnd}:t>",             true)
                .AddField("Duration",   FormatDuration(startUtc, endUtc), true);

            if (!string.IsNullOrWhiteSpace(desc))
                embedBuilder.AddField("Description", desc);

            if (overlaps.Count > 0)
            {
                var overlapSummary = string.Join("\n", overlaps.Select(e =>
                {
                    var eStart = GoogleCalendarService.ParseEventTime(e.Start);
                    var timeStr = eStart.HasValue
                        ? $"<t:{new DateTimeOffset(eStart.Value).ToUnixTimeSeconds()}:F>"
                        : "unknown time";
                    return $"• **{e.Summary}** — {timeStr}";
                }));

                embedBuilder.AddField(
                    "⚠️ Schedule Conflict",
                    $"This event overlaps with the following:\n{overlapSummary}\n\n" +
                    "The event was still created. Please coordinate with the organizer(s) of the conflicting event(s).");
            }

            embedBuilder
                .WithFooter("ClanGuard Bot • Comp Division Calendar")
                .WithTimestamp(DateTimeOffset.UtcNow);

            await modal.FollowupAsync(embed: embedBuilder.Build(), ephemeral: true);

            _logger.LogInformation(
                "Comp event '{EventName}' created by {Creator} — {Start} to {End} UTC (overlaps: {Overlaps})",
                eventName, creatorName, startUtc, endUtc, overlaps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create comp event for {User}", modal.User.Username);
            await modal.FollowupAsync(
                "❌ Failed to create the event. Please try again or contact an admin.",
                ephemeral: true);
        }
    }

    // ─── Permission Check ────────────────────────────────────────────

    /// <summary>
    /// Returns true if the user holds a rank at or above CompEventMinRank in the configured
    /// rank hierarchy, or if they have Administrator / Manage Roles permissions.
    /// </summary>
    private bool HasCompEventPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minRank   = _config.CompEventMinRank;
        var minIndex  = rankRoles.IndexOf(minRank);

        if (minIndex < 0)
        {
            _logger.LogWarning(
                "CompEventMinRank '{MinRank}' not found in RankRoles — command will be admin-only",
                minRank);
            return false;
        }

        var highestUserIndex = user.Roles
            .Select(r => rankRoles.IndexOf(r.Name))
            .DefaultIfEmpty(-1)
            .Max();

        return highestUserIndex >= minIndex;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses a date string (MM/DD/YYYY or YYYY-MM-DD) and time string (HH:MM, 24-hour)
    /// into a UTC DateTime. Returns false if either cannot be parsed.
    /// </summary>
    private static bool TryParseEventDateTime(string dateStr, string timeStr, out DateTime result)
    {
        result = DateTime.MinValue;

        // Normalise separators so both MM/DD/YYYY and MM-DD-YYYY work
        dateStr = dateStr.Replace('-', '/');
        timeStr = timeStr.Replace('.', ':');

        if (!DateOnly.TryParse(dateStr, out var date)) return false;
        if (!TimeOnly.TryParse(timeStr, out var time)) return false;

        result = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Utc);
        return true;
    }

    private static string FormatDuration(DateTime start, DateTime end)
    {
        var dur = end - start;
        if (dur.TotalMinutes < 60)
            return $"{(int)dur.TotalMinutes}m";
        if (dur.Minutes == 0)
            return $"{(int)dur.TotalHours}h";
        return $"{(int)dur.TotalHours}h {dur.Minutes}m";
    }
}
