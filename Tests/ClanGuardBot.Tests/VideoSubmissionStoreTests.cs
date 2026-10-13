using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Tests;

public class VideoSubmissionStoreTests
{
    private const ulong UploadChannel = 500;
    private const ulong ClipChannel   = 600;

    /// <summary>A clip forwarded to the upload channel (the new flow); one row per button kind.</summary>
    private static VideoSubmission Forwarded(
        ulong replyId, ulong originalId, int index, VideoSubmissionStatus status = VideoSubmissionStatus.Pending,
        VideoSubmissionKind kind = VideoSubmissionKind.Video) => new()
    {
        GuildId = 1, ChannelId = UploadChannel, MessageId = replyId, AttachmentIndex = index, Kind = kind,
        KeptOriginal = true, OriginalChannelId = ClipChannel, OriginalMessageId = originalId,
        FileName = $"clip{index}.mp4", Status = status,
    };

    /// <summary>A row from before forwarding: a repost, or an in-place reply when keptOriginal.</summary>
    private static VideoSubmission Legacy(
        ulong buttonMessageId, ulong originalId, int index, bool keptOriginal,
        VideoSubmissionStatus status = VideoSubmissionStatus.Pending) => new()
    {
        GuildId = 1, ChannelId = ClipChannel, MessageId = buttonMessageId, AttachmentIndex = index,
        KeptOriginal = keptOriginal, OriginalMessageId = originalId,
        FileName = $"clip{index}.mp4", Status = status,
    };

    private static async Task SeedAsync(TestDb test, params VideoSubmission[] rows)
    {
        await using var db = test.NewContext();
        db.VideoSubmissions.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private static async Task<VideoSubmission> RowAsync(
        TestDb test, ulong messageId, int index, VideoSubmissionKind kind = VideoSubmissionKind.Video)
    {
        await using var db = test.NewContext();
        return await db.VideoSubmissions.AsNoTracking()
            .SingleAsync(v => v.MessageId == messageId && v.AttachmentIndex == index && v.Kind == kind);
    }

    // ─── TryClaimAsync ───────────────────────────────────────────────

    [Fact]
    public async Task TryClaim_first_click_wins_and_a_second_click_loses()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0));
        var now = DateTime.UtcNow;

        await using (var db = test.NewContext())
            Assert.True(await VideoSubmissionStore.TryClaimAsync(db, 10, 0, VideoSubmissionKind.Video, 7, "Alice", now));
        await using (var db = test.NewContext())
            Assert.False(await VideoSubmissionStore.TryClaimAsync(db, 10, 0, VideoSubmissionKind.Video, 8, "Bob", now));

        var row = await RowAsync(test, 10, 0);
        Assert.Equal(VideoSubmissionStatus.Uploading, row.Status);
        Assert.Equal(7ul, row.UploadedByUserId);
        Assert.Equal("Alice", row.UploadedByName);
    }

    [Fact]
    public async Task TryClaim_refuses_a_video_whose_original_was_deleted()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0, VideoSubmissionStatus.OriginalDeleted));

        await using var db = test.NewContext();
        Assert.False(await VideoSubmissionStore.TryClaimAsync(db, 10, 0, VideoSubmissionKind.Video, 7, "Alice", DateTime.UtcNow));
    }

    // ─── MarkOriginalDeletedAsync ────────────────────────────────────

    [Fact]
    public async Task MarkOriginalDeleted_marks_only_the_pending_videos_of_that_post()
    {
        using var test = new TestDb();
        await SeedAsync(test,
            Forwarded(10, 100, 0),
            Forwarded(10, 100, 1, VideoSubmissionStatus.Uploading),
            Forwarded(10, 100, 2, VideoSubmissionStatus.Uploaded),
            Forwarded(20, 200, 0));

        IReadOnlyList<(ulong ChannelId, ulong MessageId)> replies;
        await using (var db = test.NewContext())
            replies = await VideoSubmissionStore.MarkOriginalDeletedAsync(db, 100);

        Assert.Equal(new[] { (UploadChannel, 10ul) }, replies);
        Assert.Equal(VideoSubmissionStatus.OriginalDeleted, (await RowAsync(test, 10, 0)).Status);
        Assert.Equal(VideoSubmissionStatus.Uploading,       (await RowAsync(test, 10, 1)).Status);
        Assert.Equal(VideoSubmissionStatus.Uploaded,        (await RowAsync(test, 10, 2)).Status);
        Assert.Equal(VideoSubmissionStatus.Pending,         (await RowAsync(test, 20, 0)).Status);
    }

    [Fact]
    public async Task MarkOriginalDeleted_leaves_legacy_rows_alone()
    {
        using var test = new TestDb();
        await SeedAsync(test,
            Legacy(30, 300, 0, keptOriginal: true),
            Legacy(31, 301, 0, keptOriginal: false));

        await using (var db = test.NewContext())
        {
            Assert.Empty(await VideoSubmissionStore.MarkOriginalDeletedAsync(db, 300));
            Assert.Empty(await VideoSubmissionStore.MarkOriginalDeletedAsync(db, 301));
        }

        Assert.Equal(VideoSubmissionStatus.Pending, (await RowAsync(test, 30, 0)).Status);
        Assert.Equal(VideoSubmissionStatus.Pending, (await RowAsync(test, 31, 0)).Status);
    }

    [Fact]
    public async Task MarkOriginalDeleted_returns_nothing_when_nothing_was_pending()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0, VideoSubmissionStatus.Uploaded));

        await using var db = test.NewContext();
        Assert.Empty(await VideoSubmissionStore.MarkOriginalDeletedAsync(db, 100));
    }

    // ─── ReleaseFailedUploadAsync ────────────────────────────────────

    [Fact]
    public async Task ReleaseFailedUpload_returns_an_uploading_video_to_pending_with_the_error()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0, VideoSubmissionStatus.Uploading));

        await using (var db = test.NewContext())
            Assert.Equal(1, await VideoSubmissionStore.ReleaseFailedUploadAsync(db, 10, 0, VideoSubmissionKind.Video, "quota", originalDeleted: false));

        var row = await RowAsync(test, 10, 0);
        Assert.Equal(VideoSubmissionStatus.Pending, row.Status);
        Assert.Equal("quota", row.LastError);
    }

    [Fact]
    public async Task ReleaseFailedUpload_marks_the_video_deleted_when_the_original_is_gone()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0, VideoSubmissionStatus.Uploading));

        await using (var db = test.NewContext())
            await VideoSubmissionStore.ReleaseFailedUploadAsync(db, 10, 0, VideoSubmissionKind.Video, "gone", originalDeleted: true);

        Assert.Equal(VideoSubmissionStatus.OriginalDeleted, (await RowAsync(test, 10, 0)).Status);
    }

    [Fact]
    public async Task ReleaseFailedUpload_leaves_a_video_that_is_not_uploading_alone()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0, VideoSubmissionStatus.Uploaded));

        await using (var db = test.NewContext())
            Assert.Equal(0, await VideoSubmissionStore.ReleaseFailedUploadAsync(db, 10, 0, VideoSubmissionKind.Video, "late", originalDeleted: true));

        Assert.Equal(VideoSubmissionStatus.Uploaded, (await RowAsync(test, 10, 0)).Status);
    }

    [Fact]
    public async Task ReleaseFailedUpload_keeps_at_most_500_characters_of_the_error()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0, VideoSubmissionStatus.Uploading));

        await using (var db = test.NewContext())
            await VideoSubmissionStore.ReleaseFailedUploadAsync(db, 10, 0, VideoSubmissionKind.Video, new string('x', 900), originalDeleted: false);

        Assert.Equal(500, (await RowAsync(test, 10, 0)).LastError!.Length);
    }

    // ─── Two kinds per video ─────────────────────────────────────────

    [Fact]
    public async Task Claiming_the_short_leaves_the_video_row_alone_and_saves_its_settings()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0), Forwarded(10, 100, 0, kind: VideoSubmissionKind.Short));
        var settings = new ShortSettings(ShortLayout.CenterCrop, 5, 15, "Nice shot");

        await using (var db = test.NewContext())
            Assert.True(await VideoSubmissionStore.TryClaimAsync(
                db, 10, 0, VideoSubmissionKind.Short, 7, "Alice", DateTime.UtcNow, settings));

        var shortRow = await RowAsync(test, 10, 0, VideoSubmissionKind.Short);
        Assert.Equal(VideoSubmissionStatus.Uploading, shortRow.Status);
        Assert.Equal(ShortLayout.CenterCrop, shortRow.ShortLayout);
        Assert.Equal(5, shortRow.ShortStartSeconds);
        Assert.Equal(15, shortRow.ShortEndSeconds);
        Assert.Equal("Nice shot", shortRow.ShortTitle);
        Assert.Equal(VideoSubmissionStatus.Pending, (await RowAsync(test, 10, 0)).Status);
    }

    [Fact]
    public async Task Claiming_the_video_leaves_the_short_row_alone()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0), Forwarded(10, 100, 0, kind: VideoSubmissionKind.Short));

        await using (var db = test.NewContext())
            Assert.True(await VideoSubmissionStore.TryClaimAsync(db, 10, 0, VideoSubmissionKind.Video, 7, "Alice", DateTime.UtcNow));

        Assert.Equal(VideoSubmissionStatus.Uploading, (await RowAsync(test, 10, 0)).Status);
        Assert.Equal(VideoSubmissionStatus.Pending, (await RowAsync(test, 10, 0, VideoSubmissionKind.Short)).Status);
    }

    [Fact]
    public async Task ReleaseFailedUpload_only_touches_its_own_kind()
    {
        using var test = new TestDb();
        await SeedAsync(test,
            Forwarded(10, 100, 0, VideoSubmissionStatus.Uploading),
            Forwarded(10, 100, 0, VideoSubmissionStatus.Uploading, VideoSubmissionKind.Short));

        await using (var db = test.NewContext())
            Assert.Equal(1, await VideoSubmissionStore.ReleaseFailedUploadAsync(
                db, 10, 0, VideoSubmissionKind.Short, "render failed", originalDeleted: false));

        Assert.Equal(VideoSubmissionStatus.Pending, (await RowAsync(test, 10, 0, VideoSubmissionKind.Short)).Status);
        Assert.Equal(VideoSubmissionStatus.Uploading, (await RowAsync(test, 10, 0)).Status);
    }

    [Fact]
    public async Task MarkOriginalDeleted_marks_both_kinds()
    {
        using var test = new TestDb();
        await SeedAsync(test, Forwarded(10, 100, 0), Forwarded(10, 100, 0, kind: VideoSubmissionKind.Short));

        await using (var db = test.NewContext())
            Assert.Single(await VideoSubmissionStore.MarkOriginalDeletedAsync(db, 100));

        Assert.Equal(VideoSubmissionStatus.OriginalDeleted, (await RowAsync(test, 10, 0)).Status);
        Assert.Equal(VideoSubmissionStatus.OriginalDeleted, (await RowAsync(test, 10, 0, VideoSubmissionKind.Short)).Status);
    }

    [Fact]
    public async Task CountVideos_counts_video_rows_only()
    {
        using var test = new TestDb();
        await SeedAsync(test,
            Forwarded(10, 100, 0), Forwarded(10, 100, 0, kind: VideoSubmissionKind.Short),
            Forwarded(10, 100, 1), Forwarded(10, 100, 1, kind: VideoSubmissionKind.Short));

        await using var db = test.NewContext();
        Assert.Equal(2, await VideoSubmissionStore.CountVideosAsync(db, 10));
    }
}
