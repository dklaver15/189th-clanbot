using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The bot message that carries a clip's Upload button(s): its text (for clips
/// forwarded to the upload channel) and its buttons. Both are built from the
/// <see cref="VideoSubmission"/> rows alone, so any redraw can rebuild the
/// message from the database.
/// </summary>
internal static class VideoUploadCard
{
    /// <summary>Button custom id prefix; the attachment index follows.</summary>
    public const string CustomIdPrefix = "vidup:";

    /// <summary>Short button custom id prefix; the attachment index follows.</summary>
    public const string ShortCustomIdPrefix = "vidshort:";

    public const string OriginalDeletedLine = "🗑️ Original post deleted, so this clip can't be uploaded.";

    /// <summary>
    /// The reply under a forwarded clip. Every row of one post shares the fields
    /// used here, so any of them will do.
    /// </summary>
    public static string BuildText(VideoSubmission video, bool originalDeleted)
    {
        var jumpUrl = $"https://discord.com/channels/{video.GuildId}/{video.OriginalChannelId}/{video.OriginalMessageId}";
        var text = $"🎬 Clip by <@{video.PosterUserId}> in <#{video.OriginalChannelId}> · [Jump to post]({jumpUrl})";
        return originalDeleted ? text + "\n" + OriginalDeletedLine : text;
    }

    /// <summary>
    /// The buttons for every video, in attachment order: Upload, then Short when
    /// the video has a Short row. Numbered only when the post carries more than one
    /// video. Discord allows 5 buttons per row and 5 rows: with Short buttons two
    /// videos share a row (10 videos = 5 rows of 4); without them, as before, up to
    /// 5 Upload buttons per row.
    /// </summary>
    public static MessageComponent BuildComponents(IReadOnlyList<VideoSubmission> rows)
    {
        var builder   = new ComponentBuilder();
        var videos    = rows.GroupBy(r => r.AttachmentIndex).OrderBy(g => g.Key).ToList();
        var numbered  = videos.Count > 1;
        var hasShorts = rows.Any(r => r.Kind == VideoSubmissionKind.Short);

        for (var n = 0; n < videos.Count; n++)
        {
            var suffix = numbered ? $" #{n + 1}" : "";
            var row    = hasShorts ? n / 2 : n / 5;

            if (videos[n].FirstOrDefault(r => r.Kind == VideoSubmissionKind.Video) is { } video)
                AddUploadButton(builder, video, suffix, row);
            if (videos[n].FirstOrDefault(r => r.Kind == VideoSubmissionKind.Short) is { } shortRow)
                AddShortButton(builder, shortRow, suffix, row);
        }

        return builder.Build();
    }

    private static void AddUploadButton(ComponentBuilder builder, VideoSubmission v, string suffix, int row)
    {
        var id = CustomIdPrefix + v.AttachmentIndex;
        switch (v.Status)
        {
            case VideoSubmissionStatus.Uploaded:
                builder.WithButton("Watch on YouTube" + suffix, style: ButtonStyle.Link,
                    url: $"https://youtu.be/{v.YouTubeVideoId}", row: row);
                break;
            case VideoSubmissionStatus.Uploading:
                builder.WithButton("Uploading…" + suffix, id, ButtonStyle.Secondary, disabled: true, row: row);
                break;
            case VideoSubmissionStatus.OriginalDeleted:
                builder.WithButton("Original deleted" + suffix, id, ButtonStyle.Secondary, disabled: true, row: row);
                break;
            default:
                builder.WithButton("Upload" + suffix, id, ButtonStyle.Primary, emote: new Emoji("📤"), row: row);
                break;
        }
    }

    private static void AddShortButton(ComponentBuilder builder, VideoSubmission v, string suffix, int row)
    {
        var id = ShortCustomIdPrefix + v.AttachmentIndex;
        switch (v.Status)
        {
            case VideoSubmissionStatus.Uploaded:
                builder.WithButton("Watch Short" + suffix, style: ButtonStyle.Link, emote: new Emoji("▶️"),
                    url: $"https://youtube.com/shorts/{v.YouTubeVideoId}", row: row);
                break;
            case VideoSubmissionStatus.Uploading:
                builder.WithButton("Making Short…" + suffix, id, ButtonStyle.Secondary, disabled: true, row: row);
                break;
            case VideoSubmissionStatus.OriginalDeleted:
                builder.WithButton("Original deleted" + suffix, id, ButtonStyle.Secondary, disabled: true, row: row);
                break;
            default:
                builder.WithButton("Short" + suffix, id, ButtonStyle.Secondary, emote: new Emoji("📱"), row: row);
                break;
        }
    }
}
