namespace ClanGuardBot.Models;

/// <summary>
/// One fantasy week's posted messages, so the posting service survives a restart
/// without duplicating or losing anything.
///
/// ── Why this is persisted and not held in memory ──
/// The three surfaces for a week (preview, live scoreboard, recap) are spread over
/// four days. The bot restarts far more often than that, on every deploy. Holding
/// the message ids in memory would mean a Tuesday deploy re-posts Sunday's preview
/// and orphans the live scoreboard it was editing, which is exactly the class of
/// bug the event reminders hit before ClanEvent.LastReminderMessageId was added.
///
/// One row per (GuildId, Season, Week). The three message ids are each nullable
/// because they appear at different points in the week, and a null id is the
/// service's "not posted yet" signal for that surface.
/// </summary>
public class SleeperWeekPost
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    /// <summary>The NFL season, as Sleeper reports it, e.g. "2026".</summary>
    public string Season { get; set; } = string.Empty;

    /// <summary>The NFL week number this row covers.</summary>
    public int Week { get; set; }

    /// <summary>Message id of the week's matchup preview. Null until it posts.</summary>
    public ulong? PreviewMessageId { get; set; }

    /// <summary>
    /// Message id of the live scoreboard, edited in place on every cycle while the
    /// week is live. Null until the first score lands.
    /// </summary>
    public ulong? ScoreboardMessageId { get; set; }

    /// <summary>Message id of the end-of-week recap. Non-null means this week is closed out.</summary>
    public ulong? RecapMessageId { get; set; }

    /// <summary>
    /// The channel the three messages went to. Stored per row so changing
    /// SleeperChannelId mid-season does not make the service try to edit a message
    /// in a channel it no longer posts to.
    /// </summary>
    public ulong ChannelId { get; set; }

    /// <summary>
    /// Hash of the scoreboard body at the last edit. The service compares against
    /// this and skips the edit when nothing moved, so a quiet Tuesday does not
    /// spend a Discord edit every cycle.
    /// </summary>
    public string ScoreboardHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
