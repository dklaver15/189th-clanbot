namespace ClanGuardBot.Models;

/// <summary>
/// One authored event occurrence, owned by ClanGuard (this is the in-house
/// replacement for an Apollo post). Each ClanEvent owns exactly one
/// <see cref="CalendarEvent"/> hub row — created with Source="Clan" and pushed
/// to Google Calendar through the existing CalendarOutbox pipeline — so all
/// downstream behaviour (GCal sync, voice-based attendance credit,
/// auto-promotion) is identical to an Apollo-sourced event. ClanEvent only
/// adds the things we now own that Apollo used to provide: the editable RSVP
/// message, cosmetic signups, recurrence linkage, and reminder bookkeeping.
///
/// ── Relationship to CalendarEvent ──
/// ClanEvent is the authored source; CalendarEvent is the calendar/attendance
/// projection it drives. This mirrors the existing ApolloEvent → CalendarEvent
/// layering and deliberately keeps RSVP / recurrence / reminder / message-
/// ownership concerns OFF the shared CalendarEvent hub (which is also written
/// by Apollo and /comp-event).
///
/// ── Times ──
/// StartUtc / EndUtc are UTC, matching every other ...Utc column. They are the
/// single source of truth for the instant. The #events embed never displays
/// these as a raw date — it emits Discord &lt;t:unix:F&gt; markdown, which each
/// client localizes to the viewer's own timezone.
/// </summary>
public class ClanEvent
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>
    /// FK to <see cref="ClanEventSeries"/>.Id when this occurrence was
    /// materialized from a recurring series; null for one-off events. SQLite
    /// treats NULLs as distinct, so the unique (SeriesId, StartUtc) index that
    /// guards recurrence idempotency does not constrain one-off rows.
    /// </summary>
    public int? SeriesId { get; set; }

    /// <summary>
    /// FK to <see cref="CalendarEvent"/>.Id — the hub row this occurrence
    /// drives. Not a navigation property by design (matches the loose-coupling
    /// style of EventAttendance.CalendarEventId); used to enqueue Update/Delete
    /// outbox rows when the event is edited or cancelled.
    /// </summary>
    public int CalendarEventId { get; set; }

    /// <summary>Channel the RSVP post lives in (currently always EventsTextChannelId).</summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// The bot's own RSVP message. Because we own it, RSVP clicks edit it in
    /// place and the button-locking sweep can disable its components after
    /// StartUtc. This is the key difference from Apollo, where we only ever
    /// observed someone else's message.
    /// </summary>
    public ulong MessageId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ulong OrganizerId { get; set; }
    public string OrganizerName { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    public ClanEventStatus Status { get; set; } = ClanEventStatus.Scheduled;

    /// <summary>
    /// CSV of reminder lead-minute values that have already fired for this
    /// occurrence (e.g. "60,15"). Empty means none fired yet. Stored in the DB
    /// rather than in memory so reminder state survives a `docker restart`
    /// (same lesson as the BumpReminderHandler restart-safe fix). Cleared by
    /// /event edit when the start time moves, so reminders re-arm to the new
    /// time.
    /// </summary>
    public string RemindersSentCsv { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Stamped when Status flips to Cancelled.</summary>
    public DateTime? CancelledAt { get; set; }
}

public enum ClanEventStatus
{
    Scheduled = 1,
    Cancelled = 2,

    /// <summary>
    /// Event has ended and its #events post was auto-deleted by
    /// EventArchiveService. The CalendarEvent row and Google Calendar entry are
    /// kept; only the Discord message is removed. The row remains so the
    /// recurrence scheduler still treats the slot as filled.
    /// </summary>
    Archived = 3,
}
