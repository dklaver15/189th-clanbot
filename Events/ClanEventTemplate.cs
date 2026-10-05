namespace ClanGuardBot.Models;

/// <summary>
/// A reusable, time-independent event preset. Officers (CPT+ by default — see
/// <see cref="BotConfig.EventTemplateManageMinRank"/>) save the boilerplate of a
/// recurring kind of event once — title, description, length, attendee cap,
/// banner — so creating the next instance is just "pick the template, say when,
/// say who's hosting".
///
/// Conceptually a template is a persisted <see cref="EventDraft"/> minus the
/// concrete time/recurrence/host: those are supplied at use-time. Using a
/// template fills an <see cref="EventDraft"/> from these fields and hands it to
/// <see cref="ClanGuardBot.Services.IEventPublisher.PublishOneOffAsync"/>, so a
/// templated event behaves exactly like one built from scratch in the wizard
/// (GCal sync, RSVP buttons, attendance credit, reminders — all unchanged).
/// </summary>
public class ClanEventTemplate
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>
    /// The label officers pick from (e.g. "Friday Night Ops"). Unique per guild
    /// is enforced case-insensitively in the save flow rather than by a DB
    /// constraint (SQLite's default TEXT collation is case-sensitive, which would
    /// let "ops" and "Ops" both through).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The event title stamped onto each created occurrence.</summary>
    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Event length in minutes; becomes EndUtc = StartUtc + this.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>Optional Going cap inherited by created events; null = unlimited.</summary>
    public int? MaxParticipants { get; set; }

    /// <summary>Optional banner image bytes, copied onto each created event's post.</summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>Attachment file name for <see cref="ImageBytes"/>.</summary>
    public string? ImageFileName { get; set; }

    /// <summary>Who saved the template (for display in the list).</summary>
    public ulong CreatedById { get; set; }
    public string CreatedByName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
