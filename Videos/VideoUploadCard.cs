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
    /// One button per video, in attachment order. Numbered only when the post
    /// carries more than one video. Up to 5 buttons per row (Discord's cap);
    /// a message carries at most 10 attachments, so two rows at most.
    /// </summary>
    public static MessageComponent BuildComponents(IReadOnlyList<VideoSubmission> videos)
    {
        var builder  = new ComponentBuilder();
        var numbered = videos.Count > 1;
        var ordered  = videos.OrderBy(v => v.AttachmentIndex).ToList();

        for (var n = 0; n < ordered.Count; n++)
        {
            var v      = ordered[n];
            var suffix = numbered ? $" #{n + 1}" : "";
            var row    = n / 5;
            var id     = CustomIdPrefix + v.AttachmentIndex;

            switch (v.Status)
            {
                case VideoSubmissionStatus.Uploaded:
                    builder.WithButton("Watch on YouTube" + suffix, style: ButtonStyle.Link,
                        url: $"https://youtu.be/{v.YouTubeVideoId}", row: row);
                    break;
                case VideoSubmissionStatus.Uploading:
                    builder.WithButton("Uploading…" + suffix, id, ButtonStyle.Secondary,
                        disabled: true, row: row);
                    break;
                case VideoSubmissionStatus.OriginalDeleted:
                    builder.WithButton("Original deleted" + suffix, id, ButtonStyle.Secondary,
                        disabled: true, row: row);
                    break;
                default:
                    builder.WithButton("Upload" + suffix, id, ButtonStyle.Primary,
                        emote: new Emoji("📤"), row: row);
                    break;
            }
        }

        return builder.Build();
    }
}
