using ClanGuardBot.Models;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Wraps the Google Calendar v3 API for ClanGuard's two event sources:
///   - "Clan"    — clan-wide events parsed from Apollo bot posts (teal/Peacock color)
///   - "CompDiv" — competitive division events created via /comp-event command (red/Tomato color)
///
/// The service account in google-credentials.json must have at least "Make changes to events"
/// permission on the target calendar.
/// </summary>
public class GoogleCalendarService
{
    private readonly CalendarService _calendar;
    private readonly ILogger<GoogleCalendarService> _logger;
    private readonly BotConfig _config;

    public GoogleCalendarService(
        ILogger<GoogleCalendarService> logger,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;

        var credential = GoogleCredential
            .FromFile(_config.GoogleCredentialsPath)
            .CreateScoped(CalendarService.Scope.Calendar);

        _calendar = new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "ClanGuardBot"
        });
    }

    // ─── Event CRUD ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a new calendar event and returns the created Event object (including its ID).
    /// </summary>
    /// <param name="title">Event title / summary.</param>
    /// <param name="startUtc">Start time in UTC.</param>
    /// <param name="endUtc">End time in UTC.</param>
    /// <param name="description">Optional body text.</param>
    /// <param name="creatorName">Display name of the person who triggered creation (prepended to description).</param>
    /// <param name="source">"Clan" or "CompDiv" — controls the calendar color.</param>
    public async Task<Event> CreateEventAsync(
        string title,
        DateTime startUtc,
        DateTime endUtc,
        string description = "",
        string creatorName = "",
        string source = "Clan")
    {
        var body = string.IsNullOrWhiteSpace(creatorName)
            ? description
            : $"Created by: {creatorName}\n\n{description}".TrimEnd();

        // Google Calendar color IDs: 7 = Peacock (teal) for clan, 11 = Tomato (red) for comp
        var colorId = source == "CompDiv" ? "11" : "7";

        var calEvent = new Event
        {
            Summary     = title,
            Description = body,
            ColorId     = colorId,
            Start = new EventDateTime
            {
                DateTimeRaw = Rfc3339(startUtc),
                TimeZone    = "UTC"
            },
            End = new EventDateTime
            {
                DateTimeRaw = Rfc3339(endUtc),
                TimeZone    = "UTC"
            },
            // Tag the source so you can tell clan vs comp events apart in the API / webhooks
            ExtendedProperties = new Event.ExtendedPropertiesData
            {
                Private__ = new Dictionary<string, string> { ["source"] = source }
            }
        };

        var result = await _calendar.Events.Insert(calEvent, _config.GoogleCalendarId).ExecuteAsync();

        _logger.LogInformation(
            "Calendar event created: '{Title}' [{Source}] {Start}–{End} UTC (id={EventId})",
            title, source, startUtc, endUtc, result.Id);

        return result;
    }

    /// <summary>
    /// Updates the title and time of an existing calendar event.
    /// Returns the updated Event, or null on failure.
    /// </summary>
    public async Task<Event?> UpdateEventAsync(
        string calendarEventId,
        string title,
        DateTime startUtc,
        DateTime endUtc,
        string? description = null)
    {
        try
        {
            var existing = await _calendar.Events
                .Get(_config.GoogleCalendarId, calendarEventId)
                .ExecuteAsync();

            existing.Summary = title;
            existing.Start   = new EventDateTime { DateTimeRaw = Rfc3339(startUtc), TimeZone = "UTC" };
            existing.End     = new EventDateTime { DateTimeRaw = Rfc3339(endUtc),   TimeZone = "UTC" };

            if (description is not null)
                existing.Description = description;

            var result = await _calendar.Events
                .Update(existing, _config.GoogleCalendarId, calendarEventId)
                .ExecuteAsync();

            _logger.LogInformation(
                "Calendar event updated: '{Title}' ({EventId})", title, calendarEventId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update calendar event {EventId}", calendarEventId);
            return null;
        }
    }

    /// <summary>
    /// Deletes a calendar event by its Google Calendar event ID.
    /// </summary>
    public async Task DeleteEventAsync(string calendarEventId)
    {
        try
        {
            await _calendar.Events
                .Delete(_config.GoogleCalendarId, calendarEventId)
                .ExecuteAsync();

            _logger.LogInformation("Calendar event deleted: {EventId}", calendarEventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete calendar event {EventId}", calendarEventId);
        }
    }

    // ─── Overlap Detection ───────────────────────────────────────────

    /// <summary>
    /// Returns all calendar events whose time window overlaps with [startUtc, endUtc].
    /// An event overlaps if it starts before our end AND ends after our start.
    /// </summary>
    public async Task<List<Event>> GetOverlappingEventsAsync(DateTime startUtc, DateTime endUtc)
    {
        var request = _calendar.Events.List(_config.GoogleCalendarId);
        request.TimeMinDateTimeOffset = new DateTimeOffset(startUtc, TimeSpan.Zero);
        request.TimeMaxDateTimeOffset = new DateTimeOffset(endUtc,   TimeSpan.Zero);
        request.SingleEvents          = true;
        request.OrderBy               = EventsResource.ListRequest.OrderByEnum.StartTime;

        var response = await request.ExecuteAsync();
        var items    = response.Items ?? new List<Event>();

        return items.Where(e =>
        {
            var eStart = ParseEventTime(e.Start);
            var eEnd   = ParseEventTime(e.End);
            if (eStart is null || eEnd is null) return false;
            return eStart < endUtc && eEnd > startUtc;
        }).ToList();
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static string Rfc3339(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Parses the UTC DateTime from a Google EventDateTime (dateTime or date field).</summary>
    public static DateTime? ParseEventTime(EventDateTime? edt)
    {
        if (edt is null) return null;

        if (!string.IsNullOrEmpty(edt.DateTimeRaw) &&
            DateTime.TryParse(edt.DateTimeRaw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();

        // All-day event — treat start of day UTC
        if (!string.IsNullOrEmpty(edt.Date) &&
            DateOnly.TryParse(edt.Date, out var dateOnly))
            return dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        return null;
    }
}
