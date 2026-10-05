namespace ClanGuardBot.Models;

/// <summary>
/// Tracks the most recent successful Disboard bump per guild and when the next
/// reminder should fire. There is at most one row per guild — the GuildId index
/// in BotDbContext enforces this.
/// </summary>
public class BumpState
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>UTC timestamp of the last Disboard /bump success embed observed.</summary>
    public DateTime LastBumpAt { get; set; }

    /// <summary>
    /// UTC timestamp at which the bump reminder should next fire. Set to
    /// LastBumpAt + BumpReminderDelayHours (default 2.5h) on each observed bump.
    /// </summary>
    public DateTime NextReminderAt { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent reminder posted to the channel. Used
    /// for diagnostics and to avoid double-posting if the in-memory schedule
    /// somehow desyncs from the DB. Null until the first reminder fires after
    /// a given bump.
    /// </summary>
    public DateTime? LastReminderSentAt { get; set; }
}