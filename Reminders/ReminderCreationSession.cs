using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Steps of the DM reminder-creation wizard. Text steps are driven by DM
/// replies; Recurrence and Confirm are button-only.
/// </summary>
public enum ReminderWizardStep
{
    Timezone,        // only entered when the creator has no saved UserTimeZone
    Title,
    When,
    Description,     // optional (or 'skip')
    Image,           // optional attachment / link step (or 'skip')
    Link,            // optional URL step (or 'skip')
    Channel,         // where to post the reminder
    Ping,            // optional — roles / users / @everyone / @here (or 'skip')
    Recurrence,      // button step
    RecurrenceUntil, // typed: end date, count, or 'none'
    Confirm,         // button step
}

/// <summary>
/// The data collected by the wizard. Becomes the input to the persistence layer
/// (a <see cref="ClanReminder"/> row) on confirm. <see cref="FirstFireUtc"/> is
/// UTC; the recurrence anchor is derived from it + <see cref="TimeZoneId"/>.
/// </summary>
public sealed class ReminderDraft
{
    public ulong GuildId { get; set; }
    public ulong CreatorId { get; set; }
    public string CreatorName { get; set; } = string.Empty;

    /// <summary>Creator's IANA zone — used to interpret their input and anchor recurrence.</summary>
    public string TimeZoneId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Url { get; set; }

    /// <summary>First (or only) fire instant, UTC.</summary>
    public DateTime FirstFireUtc { get; set; }

    /// <summary>Target channel the reminder posts to.</summary>
    public ulong ChannelId { get; set; }

    /// <summary>Resolved channel name, kept only for the confirm preview.</summary>
    public string ChannelName { get; set; } = string.Empty;

    // Ping targets (all optional).
    public List<ulong> PingRoleIds { get; set; } = new();
    public List<ulong> PingUserIds { get; set; } = new();
    public bool PingEveryone { get; set; }
    public bool PingHere { get; set; }

    /// <summary>Null = one-off; otherwise a recurring reminder.</summary>
    public ClanEventFrequency? Frequency { get; set; }

    /// <summary>Recurrence bound: stop after this instant. Null with null MaxOccurrences = open-ended.</summary>
    public DateTime? UntilUtc { get; set; }

    /// <summary>Recurrence bound: stop after this many occurrences.</summary>
    public int? MaxOccurrences { get; set; }

    /// <summary>Optional banner image bytes; null = no image.</summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>Sanitized attachment file name for <see cref="ImageBytes"/>.</summary>
    public string? ImageFileName { get; set; }

    /// <summary>
    /// When set, the wizard is EDITING this existing reminder rather than creating
    /// a new one; confirm updates the row in place instead of inserting. Null for
    /// a fresh /reminder create.
    /// </summary>
    public int? EditingReminderId { get; set; }
}

/// <summary>
/// In-memory state for one creator's active wizard session. Held in a
/// ConcurrentDictionary keyed by user id; expired on idle and removed on
/// completion/cancel. A bot restart drops in-progress sessions, which is
/// acceptable — the creator simply re-runs /reminder.
/// </summary>
public sealed class ReminderCreationSession
{
    public ReminderWizardStep Step { get; set; }
    public ReminderDraft Draft { get; set; } = new();
    public IDMChannel Dm { get; set; } = null!;
    public DateTime StartedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public bool TimezoneKnown { get; set; }
}
