namespace ClanGuardBot.Models;

public enum VideoSubmissionStatus
{
    /// <summary>Reposted with a live "Upload" button.</summary>
    Pending   = 0,
    /// <summary>An uploader pressed the button and the YouTube upload is running.</summary>
    Uploading = 1,
    /// <summary>On YouTube; <see cref="VideoSubmission.YouTubeVideoId"/> is set.</summary>
    Uploaded  = 2,
    /// <summary>
    /// The member deleted their post before this video was uploaded. The forward in
    /// the upload channel stays, but the video can't be uploaded any more.
    /// </summary>
    OriginalDeleted = 3,
}

/// <summary>
/// One video from a clip post in a watched channel (see
/// <see cref="Handlers.VideoRepostHandler"/>). One row per video, so a message
/// with two clips gets two rows and two independent Upload buttons.
///
/// ── Why this is persisted ──
/// The Upload button can be pressed hours or days after the repost, across bot
/// restarts. The row carries what the YouTube title/description are built from
/// (the original text and the poster) and the upload state, which is what stops
/// two uploaders from sending the same clip twice. The video bytes themselves
/// are NOT stored: they're re-downloaded at upload time, from the member's post
/// (or, for older reposts, from the repost).
/// </summary>
public class VideoSubmission
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong ChannelId { get; set; }

    /// <summary>
    /// The bot's message carrying the Upload button: for a forwarded clip, the
    /// reply under the forward in the upload channel; for older rows, the repost
    /// or the in-place reply under the original.
    /// </summary>
    public ulong MessageId { get; set; }

    /// <summary>
    /// Position of this video in the attachment list of the message that holds
    /// the file: the member's original when <see cref="KeptOriginal"/>, the
    /// repost otherwise. Unique with <see cref="MessageId"/>, and what the
    /// button's custom id carries.
    /// </summary>
    public int AttachmentIndex { get; set; }

    /// <summary>
    /// True when the member's post stays where it is and the video is fetched
    /// from it at upload time: every forwarded clip, and older oversized posts
    /// the bot replied to in place. False only for older reposts.
    /// </summary>
    public bool KeptOriginal { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// The member's message: left in place when <see cref="KeptOriginal"/>
    /// (forwarded, or replied to in place), otherwise deleted and replaced by
    /// the repost.
    /// </summary>
    public ulong OriginalMessageId { get; set; }

    /// <summary>
    /// Channel of the member's original post, set for clips forwarded to
    /// <see cref="BotConfig.VideoUploadChannelId"/> (then <see cref="ChannelId"/>
    /// is the upload channel). Null on rows from before forwarding, whose
    /// original was in <see cref="ChannelId"/>.
    /// </summary>
    public ulong? OriginalChannelId { get; set; }

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
