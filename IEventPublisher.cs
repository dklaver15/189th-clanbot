using ClanGuardBot.Handlers;

namespace ClanGuardBot.Services;

/// <summary>
/// The seam between the creation wizard and the persistence/posting layer. The
/// wizard collects and validates an <see cref="EventDraft"/>, then hands it
/// here on confirm. Splitting this out lets the wizard (input) and the
/// publisher (CalendarEvent + outbox + RSVP embed + recurrence materialization)
/// evolve independently. Implemented by <see cref="EventPublisher"/>.
/// </summary>
public interface IEventPublisher
{
    /// <summary>Create a single, non-recurring event from the draft.</summary>
    Task PublishOneOffAsync(EventDraft draft);

    /// <summary>Create a recurring series from the draft and materialize its first occurrences.</summary>
    Task PublishSeriesAsync(EventDraft draft);
}
