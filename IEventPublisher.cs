using ClanGuardBot.Handlers;
using ClanGuardBot.Models;

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
    /// <summary>Create a Custom (specific-dates) series and post one occurrence per listed date.</summary>
    Task PublishSpecificDatesAsync(EventDraft draft);

    /// <summary>
    /// Materialize a rule-based series' occurrences within the rolling horizon
    /// (idempotent on (SeriesId, StartUtc), so already-created or already-cancelled
    /// slots are left alone). Used by the recurrence scheduler's top-up and by the
    /// edit flow after a frequency/end change. Carries the series banner onto each
    /// new occurrence. No-op for Custom series (their dates aren't rule-generated).
    /// </summary>
    Task FillHorizonAsync(ClanEventSeries series);

    /// <summary>
    /// Materialize occurrences for an explicit set of start instants under a
    /// series (idempotent per start). Used for Custom (specific-dates) series and
    /// when reconciling an edited date list. Each inherits the series' duration,
    /// banner, title, and cap.
    /// </summary>
    Task MaterializeDatesAsync(ClanEventSeries series, IEnumerable<DateTime> startsUtc);
}
