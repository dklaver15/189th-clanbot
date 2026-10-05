namespace ClanGuardBot.Models;

public enum VideoSubmissionStatus
{
    /// <summary>Reposted with a live "Upload" button.</summary>
    Pending   = 0,
    /// <summary>An uploader pressed the button and the YouTube upload is running.</summary>
    Uploading = 1,
    /// <summary>On YouTube; <see cref="VideoSubmission.YouTubeVideoId"/> is set.</summary>
    Uploaded  = 2,
}

/// <summary>
/// One video the bot reposted from a watched channel (see
/// <see cref="Handlers.VideoRepostHandler"/>). One row per video, so a message
/// with two clips gets two rows and two independent Upload buttons.
///
/// ── Why this is persisted ──
/// The Upload button can be pressed hours or days after the repost, across bot
/// restarts. The row carries what the YouTube title/description are built from
/// (the original text and the poster) and the upload state, which is what stops
/// two uploaders from sending the same clip twice. The video bytes themselves
/// are NOT stored: at upload time they're re-downloaded from the repost
/// message's attachment, whose CDN URL comes back freshly signed with the click.
/// </summary>
public class VideoSubmission
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong ChannelId { get; set; }

    /// <summary>
    /// The bot's message carrying the Upload button: the repost, or for a
    /// <see cref="KeptOriginal"/> video, the bot's reply under the original.
    /// </summary>
    public ulong MessageId { get; set; }

    /// <summary>
    /// Position of this video in the attachment list of the message that holds
    /// the file: the repost normally, the member's original when
    /// <see cref="KeptOriginal"/>. Unique with <see cref="MessageId"/>, and what
    /// the button's custom id carries.
    /// </summary>
    public int AttachmentIndex { get; set; }

    /// <summary>
    /// True when the post was too large for the bot to re-upload to Discord.
    /// The member's message is left in place, the bot replies to it with the
    /// Upload button, and the video is fetched from the original at upload time.
    /// </summary>
    public bool KeptOriginal { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The member's message: deleted and replaced by the repost, or left in
    /// place (and replied to) when <see cref="KeptOriginal"/>.
    /// </summary>
    public ulong OriginalMessageId { get; set; }

    public ulong PosterUserId { get; set; }

    /// <summary>Display name at posting time, used in the YouTube description.</summary>
    public string PosterName { get; set; } = string.Empty;

    /// <summary>The original message text with mentions resolved to names. Empty when the video was posted bare.</summary>
    public string OriginalContent { get; set; } = string.Empty;

    public DateTime PostedUtc { get; set; }

    public VideoSubmissionStatus Status { get; set; } = VideoSubmissionStatus.Pending;

    public DateTime? UploadStartedUtc { get; set; }

    public ulong? UploadedByUserId { get; set; }

    public string? UploadedByName { get; set; }

    public DateTime? UploadedUtc { get; set; }

    public string? YouTubeVideoId { get; set; }

    /// <summary>Why the most recent upload attempt failed. Cleared on success.</summary>
    public string? LastError { get; set; }
}
