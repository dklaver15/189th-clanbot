namespace ClanGuardBot.Models;

/// <summary>
/// One member's vote for one <see cref="PollOption"/>. A single-select poll holds
/// at most one row per (poll, user); a multiselect poll may hold several.
///
/// ── Why we persist votes for BOTH renderers ──
/// Even native Discord polls get persisted here: Discord discards the per-voter
/// breakdown once the message ages out, so without this the poll leaves no data
/// behind. Native rows are written from the <c>PollVoteAdded</c> /
/// <c>PollVoteRemoved</c> gateway events; anonymous rows from our own button
/// handler. For anonymous polls these rows are never surfaced in Discord — they
/// exist only to power participation / chronic-non-voter analytics.
/// </summary>
public class PollVote
{
    public int Id { get; set; }

    /// <summary>FK to <see cref="Poll"/>.Id.</summary>
    public int PollId { get; set; }

    /// <summary>FK to <see cref="PollOption"/>.Id.</summary>
    public int PollOptionId { get; set; }

    public ulong UserId { get; set; }

    public DateTime CreatedAt { get; set; }
}
