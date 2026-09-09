namespace ClanGuardBot.Models;

/// <summary>
/// A scheduled announcement ("reminder") authored via the <c>/reminder</c> DM
/// wizard and posted by <see cref="Services.ReminderSchedulerService"/> to a
/// chosen channel at <see cref="NextFireUtc"/>. This is a deliberately simpler
/// sibling of <see cref="ClanEvent"/>: no RSVP, no CalendarEvent hub, no Google
/// Calendar / attendance projection. A reminder just posts an embed (optionally
/// with an image and a link) and, optionally, pings roles / users / @everyone /
/// @here above it.
///
/// ── One row, self-rescheduling ──
/// Unlike events (which pre-materialize each occurrence as its own row), a
/// recurring reminder is a SINGLE row that reschedules itself: after each fire
/// the scheduler advances <see cref="NextFireUtc"/> to the next occurrence (or
/// flips <see cref="Status"/> to Completed for a one-off or an exhausted
/// series). The next fire instant lives in the DB, so a <c>docker restart</c>
/// never loses or double-fires a reminder (same restart-safety lesson as
/// ClanEvent.RemindersSentCsv).
///
/// ── Times ──
/// <see cref="NextFireUtc"/> / <see cref="UntilUtc"/> are true UTC instants. The
/// recurrence anchor, however, is stored as the creator's LOCAL wall-clock
/// (<see cref="FirstFireLocal"/>, Kind = Unspecified) plus <see cref="TimeZoneId"/>,
/// so every occurrence lands at the same wall-clock time year-round regardless of
/// DST — identical to <see cref="ClanEventSeries"/>. The posted embed renders
/// times as Discord &lt;t:unix&gt; markdown, localized per viewer.
/// </summary>
public class ClanReminder
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Channel the reminder is posted to (chosen in the wizard).</summary>
    public ulong ChannelId { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Optional body text (embed description). Empty = title-only.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional link surfaced in the embed (e.g. a blog post with instructions).
    /// Rendered both as the embed's clickable title URL and an explicit "More
    /// info" field so it's obviously tappable. Null/empty = no link.
    /// </summary>
    public string? Url { get; set; }

    public ulong CreatorId { get; set; }
    public string CreatorName { get; set; } = string.Empty;

    // ── Ping targets (all optional; built into the message CONTENT above the
    //    embed at fire time, because mentions inside an embed never notify) ──

    /// <summary>CSV of role ids to ping (e.g. "123,456"). Empty = none.</summary>
    public string PingRoleIdsCsv { get; set; } = string.Empty;

    /// <summary>CSV of user ids to ping. Empty = none.</summary>
    public string PingUserIdsCsv { get; set; } = string.Empty;

    /// <summary>Whether to ping @everyone.</summary>
    public bool PingEveryone { get; set; }

    /// <summary>Whether to ping @here.</summary>
    public bool PingHere { get; set; }

    // ── Schedule ──

    /// <summary>The next instant (UTC) this reminder should post. Indexed with
    /// <see cref="Status"/> for the scheduler's "due &amp; scheduled" scan.</summary>
    public DateTime NextFireUtc { get; set; }

    public ClanReminderStatus Status { get; set; } = ClanReminderStatus.Scheduled;

    // ── Recurrence (null Frequency = one-off) ──

    /// <summary>
    /// Repeat cadence, or null for a one-off. Reuses <see cref="ClanEventFrequency"/>
    /// (Daily / Weekly / Biweekly / Monthly); the Custom value is not used by
    /// reminders. Stored as an int, so the shared enum needs no migration.
    /// </summary>
    public ClanEventFrequency? Frequency { get; set; }

    /// <summary>
    /// IANA zone the recurrence is anchored to (snapshotted from the creator's
    /// UserTimeZone at creation). Also used to label the confirm/preview "read in"
    /// zone. Empty falls back to BotConfig.EventDefaultTimeZone.
    /// </summary>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// First occurrence's start as the creator's LOCAL wall-clock (Kind =
    /// Unspecified — a wall-clock label, not an instant). Combined with
    /// <see cref="TimeZoneId"/> to derive each occurrence's UTC instant DST-safely.
    /// Only meaningful when <see cref="Frequency"/> is non-null.
    /// </summary>
    public DateTime FirstFireLocal { get; set; }

    /// <summary>
    /// 0-based index of the occurrence currently scheduled in <see cref="NextFireUtc"/>.
    /// After a fire the scheduler advances this to the next future occurrence
    /// (skipping any missed while the bot was down). Bounds the series against
    /// <see cref="MaxOccurrences"/>.
    /// </summary>
    public int NextOccurrenceIndex { get; set; }

    /// <summary>Stop once an occurrence's start passes this instant (UTC). Null = unbounded by date.</summary>
    public DateTime? UntilUtc { get; set; }

    /// <summary>Hard cap on total occurrences ever fired. Null = unbounded by count.</summary>
    public int? MaxOccurrences { get; set; }

    // ── Image (stored in-DB like events, so it survives a droplet loss and the
    //    encrypted SQLite backups; attached as attachment://ImageFileName rather
    //    than a Discord CDN URL, which now expire). Null = no image. ──
    public byte[]? ImageBytes { get; set; }
    public string? ImageFileName { get; set; }

    /// <summary>
    /// Message id of the "reminder scheduled" status card posted to
    /// <see cref="ChannelId"/> at creation (so members see the reminder exists —
    /// the same idea as an event's RSVP post). 0 if none. Edited to reflect the
    /// new time on each recurring fire, flipped to a cancelled state on cancel,
    /// and deleted when a one-off fires (the real announcement replaces it).
    /// Persisted so the edit/delete still happen after a restart.
    /// </summary>
    public ulong AnnouncementMessageId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Stamped when Status flips to Cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>Stamped after the most recent successful post; null until first fire.</summary>
    public DateTime? LastFiredAt { get; set; }
}

/// <summary>
/// Presentation helper for a reminder's cadence. The raw enum name is not
/// user-facing text — <see cref="ClanEventFrequency.SixHourly"/> would render
/// as "SixHourly" in the confirm preview, the status card and <c>/reminder
/// list</c> if each of those kept interpolating the enum directly.
/// </summary>
public static class ClanReminderFrequency
{
    /// <summary>Human label for a cadence, e.g. "Every 6 hours".</summary>
    public static string Label(ClanEventFrequency freq) => freq switch
    {
        ClanEventFrequency.SixHourly => "Every 6 hours",
        ClanEventFrequency.Daily     => "Daily",
        ClanEventFrequency.Weekly    => "Weekly",
        ClanEventFrequency.Biweekly  => "Biweekly",
        ClanEventFrequency.Monthly   => "Monthly",
        _                            => freq.ToString(),
    };
}

public enum ClanReminderStatus
{
    Scheduled = 1,
    Completed = 2,
    Cancelled = 3,
}
