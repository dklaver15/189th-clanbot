using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// State changes on <see cref="VideoSubmission"/> rows. Each is one UPDATE guarded
/// on the state it moves from, so concurrent callers (two clicks, a click and a
/// deletion) can't both apply: SQLite runs writes one at a time, and the loser
/// matches 0 rows.
/// </summary>
internal static class VideoSubmissionStore
{
    private const int MaxErrorLength = 500;

    /// <summary>
    /// Moves one button's row from Pending to Uploading for this uploader. Of any
    /// number of simultaneous clicks on that button, exactly one returns true; the
    /// other kind's row is never touched. A Short claim also records the form's
    /// <paramref name="settings"/> in the same UPDATE.
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        BotDbContext db, ulong messageId, int index, VideoSubmissionKind kind,
        ulong userId, string userName, DateTime now, ShortSettings? settings = null)
    {
        ShortLayout? layout = settings?.Layout;
        double? start = settings?.StartSeconds;
        double? end = settings?.EndSeconds;
        string? title = settings?.Title;

        var rows = await db.VideoSubmissions
            .Where(v => v.MessageId == messageId && v.AttachmentIndex == index && v.Kind == kind
                     && v.Status == VideoSubmissionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status,            VideoSubmissionStatus.Uploading)
                .SetProperty(v => v.UploadStartedUtc,  (DateTime?)now)
                .SetProperty(v => v.UploadedByUserId,  (ulong?)userId)
                .SetProperty(v => v.UploadedByName,    userName)
                .SetProperty(v => v.ShortLayout,       layout)
                .SetProperty(v => v.ShortStartSeconds, start)
                .SetProperty(v => v.ShortEndSeconds,   end)
                .SetProperty(v => v.ShortTitle,        title));
        return rows == 1;
    }

    /// <summary>
    /// The member's post <paramref name="originalMessageId"/> is gone: marks its
    /// forwarded videos that are still Pending as OriginalDeleted. Uploading and
    /// Uploaded videos, and rows from before forwarding, are left alone. Returns
    /// the bot replies (channel, message) whose buttons need a redraw.
    /// </summary>
    public static async Task<IReadOnlyList<(ulong ChannelId, ulong MessageId)>> MarkOriginalDeletedAsync(
        BotDbContext db, ulong originalMessageId)
    {
        var pending = db.VideoSubmissions.Where(v =>
            v.OriginalMessageId == originalMessageId
            && v.OriginalChannelId != null
            && v.Status == VideoSubmissionStatus.Pending);

        var replies = await pending
            .Select(v => new { v.ChannelId, v.MessageId })
            .Distinct()
            .ToListAsync();
        if (replies.Count == 0) return [];

        await pending.ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, VideoSubmissionStatus.OriginalDeleted));
        return replies.Select(r => (r.ChannelId, r.MessageId)).ToList();
    }

    /// <summary>
    /// Ends a failed upload of one button: back to Pending with the error, so the
    /// button can be pressed again, or OriginalDeleted when the member's post is
    /// confirmed gone. Only moves a row of <paramref name="kind"/> that is still
    /// Uploading. Returns the rows changed (0 or 1).
    /// </summary>
    public static Task<int> ReleaseFailedUploadAsync(
        BotDbContext db, ulong messageId, int index, VideoSubmissionKind kind, string error, bool originalDeleted)
    {
        var status = originalDeleted ? VideoSubmissionStatus.OriginalDeleted : VideoSubmissionStatus.Pending;
        if (error.Length > MaxErrorLength) error = error[..MaxErrorLength];

        return db.VideoSubmissions
            .Where(v => v.MessageId == messageId && v.AttachmentIndex == index && v.Kind == kind
                     && v.Status == VideoSubmissionStatus.Uploading)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status,    status)
                .SetProperty(v => v.LastError, error));
    }

    /// <summary>
    /// How many videos the post behind <paramref name="messageId"/> carries (one
    /// Video row each; Short rows don't count). Drives the "(part N)" title suffix.
    /// </summary>
    public static Task<int> CountVideosAsync(BotDbContext db, ulong messageId) =>
        db.VideoSubmissions.CountAsync(v => v.MessageId == messageId && v.Kind == VideoSubmissionKind.Video);
}
