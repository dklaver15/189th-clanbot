using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Steps of the DM event-creation wizard. Text steps are driven by DM replies;
/// Recurrence and Confirm are button-only.
/// </summary>
public enum WizardStep
{
    Timezone,        // only entered when the organizer has no saved UserTimeZone
    Title,
    When,
    Duration,
    Description,
    Image,           // optional attachment step (or 'skip')
    MaxParticipants, // optional cap step (number, or 'none' for unlimited)
    Recurrence,      // button step
    RecurrenceUntil,
    SpecificDates,   // collecting an explicit list of dates (Custom frequency)
    Confirm,         // button step
}

/// <summary>
/// The data collected by the wizard. Becomes the input to
/// <see cref="ClanGuardBot.Services.IEventPublisher"/> on confirm. Times are
/// UTC; the publisher derives the DST-safe local anchor for a series from
/// <see cref="StartUtc"/> + <see cref="TimeZoneId"/>.
/// </summary>
public sealed class EventDraft
{
    public ulong GuildId { get; set; }
    public ulong OrganizerId { get; set; }
    public string OrganizerName { get; set; } = string.Empty;

    /// <summary>Organizer's IANA zone — used to interpret their input and to anchor recurrence.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional banner image bytes (downloaded from the organizer's DM upload); null = no image.</summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>Sanitized attachment file name for <see cref="ImageBytes"/>.</summary>
    public string? ImageFileName { get; set; }

    /// <summary>Null = one-off event; otherwise a recurring series.</summary>
    public ClanEventFrequency? Frequency { get; set; }

    /// <summary>Recurrence bound: stop after this instant. Null with null MaxOccurrences = open-ended.</summary>
    public DateTime? UntilUtc { get; set; }

    /// <summary>Recurrence bound: stop after this many occurrences.</summary>
    public int? MaxOccurrences { get; set; }

    /// <summary>
    /// For a Custom (specific-dates) series: the ADDITIONAL occurrence starts
    /// beyond the first (which is <see cref="StartUtc"/>), in UTC. Each inherits
    /// the same duration. Empty for one-offs and rule-based recurrence.
    /// </summary>
    public List<DateTime> SpecificDatesUtc { get; set; } = new();

    /// <summary>Optional Going cap; null = unlimited. For a series this becomes
    /// the series-wide cap that every occurrence inherits.</summary>
    public int? MaxParticipants { get; set; }
}

/// <summary>
/// In-memory state for one organizer's active wizard session. Held in a
/// ConcurrentDictionary keyed by organizer user id; expired on idle and removed
/// on completion/cancel. A bot restart drops in-progress sessions, which is
/// acceptable — the organizer simply re-runs /event.
/// </summary>
public sealed class EventCreationSession
{
    public WizardStep Step { get; set; }
    public EventDraft Draft { get; set; } = new();
    public IDMChannel Dm { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public bool TimezoneKnown { get; set; }

    /// <summary>
    /// Set once the organizer has been shown the "this overlaps an existing
    /// event" warning and chose to proceed anyway. Prevents the confirm step
    /// from looping on the same warning, while still catching the common
    /// mistake of creating a duplicate of an event that was never cancelled.
    /// </summary>
    public bool OverlapAcknowledged { get; set; }
}
