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
    /// Moves one video from Pending to Uploading for this uploader and records the
    /// form (video or Short, framing, trim, title) in the same UPDATE. Of any
    /// number of simultaneous submits, exactly one returns true.
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        BotDbContext db, ulong messageId, int index, ulong userId, string userName, DateTime now, UploadSettings settings)
    {
        var kind  = settings.Kind;
        ShortLayout? layout = settings.Kind == VideoSubmissionKind.Short ? settings.Layout : null;
        double? start = settings.StartSeconds;
        var end   = settings.EndSeconds;
        var title = settings.Title;

        var rows = await db.VideoSubmissions
            .Where(v => v.MessageId == messageId && v.AttachmentIndex == index
                     && v.Status == VideoSubmissionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status,           VideoSubmissionStatus.Uploading)
                .SetProperty(v => v.UploadStartedUtc, (DateTime?)now)
                .SetProperty(v => v.UploadedByUserId, (ulong?)userId)
                .SetProperty(v => v.UploadedByName,   userName)
                .SetProperty(v => v.Kind,             kind)
                .SetProperty(v => v.ShortLayout,      layout)
                .SetProperty(v => v.TrimStartSeconds, start)
                .SetProperty(v => v.TrimEndSeconds,   end)
                .SetProperty(v => v.Title,            title));
        return rows == 1;
    }

    /// <summary>
    /// The member's post <paramref name="originalMessageId"/> is gone: marks its
    /// videos that are still Pending as OriginalDeleted. Uploading and Uploaded
    /// videos, and old reposts (no OriginalChannelId), are left alone. Returns the
    /// bot replies (channel, message) to withdraw or redraw.
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
    /// Ends a failed upload: back to Pending with the error, so the button can be
    /// pressed again, or OriginalDeleted when the member's post is confirmed gone.
    /// Only moves a video that is still Uploading. Returns the rows changed (0 or 1).
    /// </summary>
    public static Task<int> ReleaseFailedUploadAsync(
        BotDbContext db, ulong messageId, int index, string error, bool originalDeleted)
    {
        var status = originalDeleted ? VideoSubmissionStatus.OriginalDeleted : VideoSubmissionStatus.Pending;
        if (error.Length > MaxErrorLength) error = error[..MaxErrorLength];

        return db.VideoSubmissions
            .Where(v => v.MessageId == messageId && v.AttachmentIndex == index
                     && v.Status == VideoSubmissionStatus.Uploading)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status,    status)
                .SetProperty(v => v.LastError, error));
    }

    /// <summary>How many videos the post behind <paramref name="messageId"/> carries. Drives the "(part N)" title suffix.</summary>
    public static Task<int> CountVideosAsync(BotDbContext db, ulong messageId) =>
        db.VideoSubmissions.CountAsync(v => v.MessageId == messageId);

    /// <summary>
    /// True when the bot reply <paramref name="messageId"/> has videos and every one
    /// of them lost its original (none uploaded, going up or still waiting).
    /// </summary>
    public static async Task<bool> NothingLeftToUploadAsync(BotDbContext db, ulong messageId)
    {
        var videos = db.VideoSubmissions.Where(v => v.MessageId == messageId);
        return await videos.AnyAsync()
            && !await videos.AnyAsync(v => v.Status != VideoSubmissionStatus.OriginalDeleted);
    }
}
