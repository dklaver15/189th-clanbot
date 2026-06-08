namespace ClanGuardBot.Models;

/// <summary>
/// Recurrence definition for a repeating event. One row per series; the
/// background scheduler materializes concrete <see cref="ClanEvent"/>
/// occurrences from it within a rolling horizon, each with its own post, RSVP
/// set, reminders, and CalendarEvent.
///
/// ── DST-safe by design (this is the important part) ──
/// Recurrence is anchored in the organizer's LOCAL wall-clock plus their
/// timezone — NOT a fixed UTC delta. A naive "next = previous StartUtc + 7
/// days" drifts by an hour across the spring/fall DST transition: a weekly
/// 10 PM Central event is 03:00 UTC under CST but 04:00 UTC under CDT, so a
/// fixed UTC step lands at the wrong wall-clock time half the year. Instead the
/// scheduler advances <see cref="FirstStartLocal"/> in local time
/// (AddDays/AddMonths), then converts that local instant to UTC fresh for each
/// occurrence via the stored <see cref="TimeZoneId"/>. Result: every
/// occurrence is 10 PM Central regardless of DST.
///
/// This is why FirstStartUtc is deliberately NOT stored — the local time +
/// zone is the canonical anchor, and any UTC value would be a derived
/// duplicate that could go stale across a DST boundary.
/// </summary>
public class ClanEventSeries
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ulong OrganizerId { get; set; }
    public string OrganizerName { get; set; } = string.Empty;

    public ClanEventFrequency Frequency { get; set; }

    /// <summary>
    /// IANA timezone id the recurrence is anchored to (e.g. "America/Chicago"),
    /// snapshotted from the organizer's UserTimeZone at creation. .NET 8 on the
    /// Ubuntu droplet resolves IANA ids via ICU. The scheduler performs all
    /// recurrence math in this zone.
    /// </summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// First occurrence's start as the organizer's LOCAL wall-clock time
    /// (Kind = Unspecified — it is a wall-clock label, not an instant).
    /// Combined with <see cref="TimeZoneId"/> to derive each occurrence's UTC
    /// instant. Stored as local-by-intent; see the class summary for why it is
    /// NOT stored as UTC.
    /// </summary>
    public DateTime FirstStartLocal { get; set; }

    /// <summary>Occurrence length in minutes; applied to each materialized occurrence's EndUtc.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>Channel each occurrence posts to (currently always EventsTextChannelId).</summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// Stop generating once an occurrence's start passes this instant. Null =
    /// open-ended (the horizon and MaxOccurrences still bound generation). This
    /// IS a true instant, so UTC is correct here — only the per-occurrence
    /// start needs local-anchored computation.
    /// </summary>
    public DateTime? UntilUtc { get; set; }

    /// <summary>Hard cap on total occurrences ever generated. Null = unbounded.</summary>
    public int? MaxOccurrences { get; set; }

    /// <summary>
    /// False once the series is retired (e.g. a "whole series" cancel). The
    /// scheduler skips inactive series; cancelling the series also tombstones
    /// its future materialized occurrences and their GCal entries.
    /// </summary>
    public bool Active { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Optional banner image bytes for the series, stored once here. Every
    /// occurrence's #events post attaches these bytes (resolved via
    /// EventImage.ResolveAsync) under <see cref="ImageFileName"/>, so the blob
    /// is not duplicated onto each materialized occurrence row.
    /// </summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>Attachment file name for the series banner image.</summary>
    public string? ImageFileName { get; set; }
}

public enum ClanEventFrequency
{
    Daily = 1,
    Weekly = 2,
    Biweekly = 3,
    Monthly = 4,
}
