namespace ClanGuardBot.Models;

/// <summary>
/// One message the bot posted on someone's behalf through <c>/say</c>.
///
/// ── Why this is persisted ──
/// It is the allow-list for <c>/say edit</c> and <c>/say delete</c>. The bot
/// authors a lot of messages it manages itself (event posts, poll embeds, the
/// XP board, the ban hammer counter), and letting an officer hand-edit one of
/// those would either be silently overwritten on the next render or corrupt the
/// state the renderer reads back. Only rows in this table can be edited, so the
/// answer to "can I edit this?" is a lookup rather than a guess about what the
/// message looks like.
///
/// It also carries the text as it was TYPED, which is what the edit modal
/// prefills from. Reading the text back off Discord would mean a REST call
/// before the modal opens, and a modal has to be the first response to the
/// interaction inside 3 seconds.
///
/// Rows are kept after a delete (DeletedUtc set) so the audit trail and the
/// original wording survive the message itself.
/// </summary>
public class SayMessage
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong ChannelId { get; set; }

    /// <summary>The posted message. Unique: one row per message.</summary>
    public ulong MessageId { get; set; }

    /// <summary>Who wrote it. The public post carries no attribution, so this is the only record.</summary>
    public ulong AuthorUserId { get; set; }

    /// <summary>Display name at posting time, kept so the audit reads correctly after a rename or a departure.</summary>
    public string AuthorName { get; set; } = string.Empty;

    /// <summary>True when the body was posted inside an embed rather than as plain text.</summary>
    public bool AsEmbed { get; set; }

    /// <summary>Embed heading. Null for a plain-text post.</summary>
    public string? Title { get; set; }

    /// <summary>The message text as typed into the modal, without any ping line.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// The rendered tags that went above the message, e.g. "@everyone" or a role
    /// mention. Preserved across an edit so fixing a typo does not silently drop
    /// the tag. Null when nothing was tagged.
    /// </summary>
    public string? PingLine { get; set; }

    public DateTime PostedUtc { get; set; }

    public DateTime? EditedUtc { get; set; }

    public ulong? EditedByUserId { get; set; }

    public string? EditedByName { get; set; }

    public DateTime? DeletedUtc { get; set; }

    public ulong? DeletedByUserId { get; set; }

    public string? DeletedByName { get; set; }
}
