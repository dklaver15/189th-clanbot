namespace ClanGuardBot.Models;

/// <summary>
/// One selectable answer on a <see cref="Poll"/>. Rows are created up front when
/// the poll is published and never change.
/// </summary>
public class PollOption
{
    public int Id { get; set; }

    /// <summary>FK to <see cref="Poll"/>.Id.</summary>
    public int PollId { get; set; }

    /// <summary>0-based display order, as the creator listed them.</summary>
    public int Position { get; set; }

    /// <summary>
    /// The id Discord assigns this answer on a NATIVE poll. Discord numbers a
    /// poll's answers 1..N in submission order, and the <c>PollVoteAdded</c>
    /// gateway event reports that id — so we store <c>Position + 1</c> here and
    /// map an incoming vote's answer id back to this option. Unused for anonymous
    /// polls (our own buttons carry the <see cref="Id"/> directly).
    /// </summary>
    public int AnswerId { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Optional leading emoji (unicode or a custom <c>&lt;:name:id&gt;</c>). Shown
    /// on the button (anonymous) or as the native answer's emoji (native).
    /// </summary>
    public string? Emoji { get; set; }
}
