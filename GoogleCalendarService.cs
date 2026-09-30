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
    /// <param name="hostName">Display name of the host (prepended as a "Host:" line); empty omits the line.</param>
    /// <param name="source">"Clan" or "CompDiv" — controls the calendar color.</param>
    public async Task<Event> CreateEventAsync(
        string title,
        DateTime startUtc,
        DateTime endUtc,
        string description = "",
        string creatorName = "",
        string hostName = "",
        string source = "Clan")
    {
        var body = BuildBody(creatorName, hostName, description);

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
    /// When <paramref name="description"/> is non-null, the body is rebuilt with
    /// the same "Created by:" / "Host:" header as creation, so an edit (or a host
    /// change) keeps those lines instead of stripping them.
    /// </summary>
    public async Task<Event?> UpdateEventAsync(
        string calendarEventId,
        string title,
        DateTime startUtc,
        DateTime endUtc,
        string? description = null,
        string creatorName = "",
        string hostName = "")
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
                existing.Description = BuildBody(creatorName, hostName, description);

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
    /// Deletes a calendar event by its Google Calendar event ID. An event that is
    /// already gone counts as deleted; any other failure throws so the caller can
    /// retry (the calendar outbox backs off and tries again).
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
        catch (Google.GoogleApiException ex)
            when (ex.HttpStatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
        {
            _logger.LogInformation("Calendar event {EventId} was already deleted", calendarEventId);
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

    // ─── Source-tagged Listing ───────────────────────────────────────

    /// <summary>
    /// Lists all clan-tagged events in the configured calendar within
    /// [fromUtc, toUtc]. Filters server-side via the
    /// extendedProperties.private.source=Clan tag we set at create time
    /// in CreateEventAsync, so CompDiv events and any non-bot calendar
    /// entries on the same calendar are excluded automatically.
    ///
    /// Used by /cleanup-calendar-dupes to find Google Calendar events
    /// that no longer have a matching CalendarEvent row in the bot's DB
    /// (orphans created by past concurrent-write races where the dup DB
    /// row got removed by ApolloBackfillService's defensive cleanup but
    /// its GCal counterpart survived).
    ///
    /// Paginates via NextPageToken to be safe across windows that exceed
    /// the API's per-page cap.
    /// </summary>
    public async Task<List<Event>> ListClanEventsAsync(DateTime fromUtc, DateTime toUtc)
    {
        var all = new List<Event>();
        string? pageToken = null;

        do
        {
            var request = _calendar.Events.List(_config.GoogleCalendarId);
            request.TimeMinDateTimeOffset   = new DateTimeOffset(fromUtc, TimeSpan.Zero);
            request.TimeMaxDateTimeOffset   = new DateTimeOffset(toUtc,   TimeSpan.Zero);
            request.SingleEvents            = true;
            request.OrderBy                 = EventsResource.ListRequest.OrderByEnum.StartTime;
            request.PrivateExtendedProperty = new[] { "source=Clan" };
            request.PageToken               = pageToken;

            var response = await request.ExecuteAsync();
            if (response.Items is not null) all.AddRange(response.Items);
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return all;
    }

    /// <summary>
    /// Lists ALL events in the configured calendar within [fromUtc, toUtc]
    /// regardless of source tag. Includes Clan-tagged events, CompDiv-tagged
    /// events, AND any events added directly via the Google Calendar UI
    /// (which won't carry an extendedProperty source tag at all).
    ///
    /// Used by /calendar to render a complete day-by-day view for clan
    /// members. The /cleanup-calendar-dupes path keeps using
    /// ListClanEventsAsync because it specifically needs to compare against
    /// the bot's CalendarEvent rows, all of which are clan-sourced.
    ///
    /// Paginates via NextPageToken to be safe across wide windows.
    /// </summary>
    public async Task<List<Event>> ListAllEventsAsync(DateTime fromUtc, DateTime toUtc)
    {
        var all = new List<Event>();
        string? pageToken = null;

        do
        {
            var request = _calendar.Events.List(_config.GoogleCalendarId);
            request.TimeMinDateTimeOffset = new DateTimeOffset(fromUtc, TimeSpan.Zero);
            request.TimeMaxDateTimeOffset = new DateTimeOffset(toUtc,   TimeSpan.Zero);
            request.SingleEvents          = true;
            request.OrderBy               = EventsResource.ListRequest.OrderByEnum.StartTime;
            request.PageToken             = pageToken;

            var response = await request.ExecuteAsync();
            if (response.Items is not null) all.AddRange(response.Items);
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return all;
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the calendar event body: an optional "Created by:" line and an
    /// optional "Host:" line, a blank line, then the description. Either header
    /// line is omitted when its name is blank. Shared by create and update so
    /// the body stays consistent across an event's lifetime.
    /// </summary>
    internal static string BuildBody(string creatorName, string hostName, string description)
    {
        var header = new List<string>();
        if (!string.IsNullOrWhiteSpace(creatorName)) header.Add($"Created by: {creatorName}");
        if (!string.IsNullOrWhiteSpace(hostName))    header.Add($"Host: {hostName}");

        var prefix = header.Count > 0 ? string.Join("\n", header) + "\n\n" : string.Empty;
        return (prefix + (description ?? string.Empty)).TrimEnd();
    }

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