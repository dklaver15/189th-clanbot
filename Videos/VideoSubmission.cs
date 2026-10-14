namespace ClanGuardBot.Models;

public enum VideoSubmissionStatus
{
    /// <summary>Forwarded with a live "Upload" button.</summary>
    Pending   = 0,
    /// <summary>An uploader submitted the form and the YouTube upload is running.</summary>
    Uploading = 1,
    /// <summary>On YouTube; <see cref="VideoSubmission.YouTubeVideoId"/> is set.</summary>
    Uploaded  = 2,
    /// <summary>
    /// The member deleted their post before this video was uploaded. The forward in
    /// the upload channel stays, but the video can't be uploaded any more.
    /// </summary>
    OriginalDeleted = 3,
}

/// <summary>What a clip went (or goes) to YouTube as.</summary>
public enum VideoSubmissionKind
{
    /// <summary>A regular YouTube video.</summary>
    Video = 0,
    /// <summary>A 9:16 YouTube Short.</summary>
    Short = 1,
}

/// <summary>How a Short frames its clip.</summary>
public enum ShortLayout
{
    /// <summary>The whole frame on top, a zoomed centre crop below (the default).</summary>
    FullViewZoom = 0,
    /// <summary>A centred 9:16 crop of the frame.</summary>
    CenterCrop = 1,
    /// <summary>A vertical or square clip kept as it is (only scaled).</summary>
    AsIs = 2,
}

/// <summary>What the uploader chose: video or Short, the Short's framing, the trim and the title.</summary>
/// <param name="Layout">The Short's framing; ignored for a video.</param>
/// <param name="EndSeconds">Null = to the end of the clip (a Short stops just under <c>UploadRequest.MaxShortSeconds</c> after the start).</param>
public sealed record UploadSettings(
    VideoSubmissionKind Kind, ShortLayout Layout, double StartSeconds, double? EndSeconds, string Title);

/// <summary>
/// One video from a clip post in a watched channel (see
/// <see cref="Handlers.VideoRepostHandler"/>). One row per video, so a forwarded
/// post with two clips gets two rows and two independent Upload buttons.
///
/// ── Why this is persisted ──
/// The Upload button can be pressed hours or days after the forward, across bot
/// restarts. The row carries what the YouTube title/description are built from
/// (the original text and the poster), what the uploader chose in the form, and
/// the upload state, which is what stops two uploaders from sending the same clip
/// twice. The video bytes themselves are NOT stored: they're re-downloaded from
/// the member's post at upload time.
/// </summary>
public class VideoSubmission
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong ChannelId { get; set; }

    /// <summary>
    /// The bot's message carrying the Upload button: the reply under the forward
    /// in the upload channel (older rows: the repost or in-place reply).
    /// </summary>
    public ulong MessageId { get; set; }

    /// <summary>
    /// Position of this video in the attachment list of the member's post. Unique
    /// with <see cref="MessageId"/>, and what the button's custom id carries.
    /// </summary>
    public int AttachmentIndex { get; set; }

    /// <summary>What the clip was uploaded as; set when the upload is claimed. Default Video.</summary>
    public VideoSubmissionKind Kind { get; set; } = VideoSubmissionKind.Video;

    /// <summary>
    /// True when the member's post stays where it is and the video is fetched
    /// from it at upload time (every forwarded clip). False only for older reposts.
    /// </summary>
    public bool KeptOriginal { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>The member's message.</summary>
    public ulong OriginalMessageId { get; set; }

    /// <summary>
    /// Channel of the member's original post, set for clips forwarded to
    /// <see cref="BotConfig.VideoUploadChannelId"/>. Null on rows from before
    /// forwarding, which can no longer be uploaded.
    /// </summary>
    public ulong? OriginalChannelId { get; set; }

    /// <summary>
    /// Size of the source video as Discord reported it when the clip was
    /// forwarded; null when it didn't. Lets the Upload button decide at once
    /// which Short framings to offer (no time to download the clip first).
    /// </summary>
    public int? Width { get; set; }

    /// <summary>See <see cref="Width"/>.</summary>
    public int? Height { get; set; }

    /// <summary>Shorts: the framing chosen.</summary>
    public ShortLayout? ShortLayout { get; set; }

    /// <summary>Where the upload starts in the clip, in seconds (from the form).</summary>
    public double? TrimStartSeconds { get; set; }

    /// <summary>Where it ends; null = end of the clip.</summary>
    public double? TrimEndSeconds { get; set; }

    /// <summary>The YouTube title from the form.</summary>
    public string? Title { get; set; }

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
