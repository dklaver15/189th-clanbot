using ClanGuardBot.Handlers;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// The seam between the creation wizard and the persistence/posting layer. The
/// wizard collects and validates an <see cref="EventDraft"/>, then hands it
/// here on confirm. Splitting this out lets the wizard (input) and the
/// publisher (CalendarEvent + outbox + RSVP embed + recurrence materialization)
/// evolve independently.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Create a single, non-recurring event from the draft.</summary>
    Task PublishOneOffAsync(EventDraft draft);

    /// <summary>Create a recurring series from the draft and materialize its first occurrences.</summary>
    Task PublishSeriesAsync(EventDraft draft);
}

/// <summary>
/// TEMPORARY scaffold implementation. Logs the draft it would publish so the
/// full DM wizard can be exercised end-to-end (via Docker logs) before the real
/// publisher exists. Replace the DI registration with the real EventPublisher
/// in the next slice — no wizard changes required.
/// </summary>
public sealed class LoggingEventPublisher : IEventPublisher
{
    private readonly ILogger<LoggingEventPublisher> _logger;

    public LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) => _logger = logger;

    public Task PublishOneOffAsync(EventDraft d)
    {
        _logger.LogInformation(
            "[event-scaffold] ONE-OFF '{Title}' {Start:o}–{End:o} UTC | organizer {Org} ({OrgId}) | guild {Guild} | tz {Tz}",
            d.Title, d.StartUtc, d.EndUtc, d.OrganizerName, d.OrganizerId, d.GuildId, d.TimeZoneId);
        return Task.CompletedTask;
    }

    public Task PublishSeriesAsync(EventDraft d)
    {
        _logger.LogInformation(
            "[event-scaffold] SERIES '{Title}' {Freq} from {Start:o} UTC | until {Until} | max {Max} | organizer {Org} ({OrgId}) | guild {Guild} | tz {Tz}",
            d.Title, d.Frequency, d.StartUtc,
            d.UntilUtc?.ToString("o") ?? "(open)", d.MaxOccurrences?.ToString() ?? "(unbounded)",
            d.OrganizerName, d.OrganizerId, d.GuildId, d.TimeZoneId);
        return Task.CompletedTask;
    }
}
