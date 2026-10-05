namespace ClanGuardBot.Models;

/// <summary>
/// A single member's cosmetic RSVP to a <see cref="ClanEvent"/>. One row per
/// (event, user); re-clicking a button updates Status in place rather than
/// inserting a second row.
///
/// ── Cosmetic only ──
/// RSVP never feeds attendance or promotion credit. Credit is earned solely by
/// voice presence in the events VC during the event window
/// (EventAttendanceSnapshotService), exactly as it was under Apollo — which
/// never exposed its RSVP roster to the parser anyway. The only behavioural
/// effect of an RSVP is the reminder ping: members whose Status is in
/// EventReminderPingStatuses get @-mentioned when the event's reminder fires.
/// </summary>
public class EventRsvp
{
    public int Id { get; set; }

    /// <summary>FK to <see cref="ClanEvent"/>.Id.</summary>
    public int ClanEventId { get; set; }

    public ulong UserId { get; set; }

    public EventRsvpStatus Status { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public enum EventRsvpStatus
{
    Going = 1,
    Maybe = 2,
    Decline = 3,

    /// <summary>
    /// Wanted to go, but the event's <see cref="ClanEvent.MaxParticipants"/> cap
    /// was already full when they clicked Going. Held in signup order (by
    /// <see cref="EventRsvp.UpdatedAt"/>) and auto-promoted to Going when a
    /// confirmed spot frees up. Stored as the int 4 — a new enum value needs no
    /// schema migration since Status is persisted as an integer.
    /// </summary>
    Waitlisted = 4,
}
