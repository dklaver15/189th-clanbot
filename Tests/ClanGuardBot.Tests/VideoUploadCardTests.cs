using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Tests;

public class VideoUploadCardTests
{
    private static VideoSubmission Video(
        int index, VideoSubmissionStatus status = VideoSubmissionStatus.Pending, string? youtubeId = null,
        VideoSubmissionKind kind = VideoSubmissionKind.Video) => new()
    {
        GuildId = 1, ChannelId = 500, MessageId = 10, AttachmentIndex = index, Kind = kind,
        KeptOriginal = true, OriginalChannelId = 600, OriginalMessageId = 100,
        PosterUserId = 42, Status = status, YouTubeVideoId = youtubeId,
    };

    private static VideoSubmission Short(
        int index, VideoSubmissionStatus status = VideoSubmissionStatus.Pending, string? youtubeId = null) =>
        Video(index, status, youtubeId, VideoSubmissionKind.Short);

    [Fact]
    public void A_single_video_gets_an_upload_and_a_short_button()
    {
        var buttons = Buttons(VideoUploadCard.BuildComponents([Video(0), Short(0)]));
        Assert.Equal(new[] { "Upload", "Short" }, buttons.Select(b => b.Label));
        Assert.Equal(new[] { "vidup:0", "vidshort:0" }, buttons.Select(b => b.CustomId));
        Assert.Equal(ButtonStyle.Secondary, buttons[1].Style);
        Assert.False(buttons[1].IsDisabled);
    }

    [Fact]
    public void Short_states_have_their_own_labels()
    {
        var making   = Buttons(VideoUploadCard.BuildComponents([Video(0), Short(0, VideoSubmissionStatus.Uploading)]))[1];
        var uploaded = Buttons(VideoUploadCard.BuildComponents([Video(0), Short(0, VideoSubmissionStatus.Uploaded, "xyz")]))[1];
        var deleted  = Buttons(VideoUploadCard.BuildComponents([Video(0), Short(0, VideoSubmissionStatus.OriginalDeleted)]))[1];

        Assert.Equal("Making Short…", making.Label);
        Assert.True(making.IsDisabled);
        Assert.Equal("Watch Short", uploaded.Label);
        Assert.Equal(ButtonStyle.Link, uploaded.Style);
        Assert.Equal("https://youtube.com/shorts/xyz", uploaded.Url);
        Assert.Equal("Original deleted", deleted.Label);
        Assert.True(deleted.IsDisabled);
        Assert.Equal("vidshort:0", deleted.CustomId);
    }

    [Fact]
    public void With_shorts_two_videos_share_a_row()
    {
        var rows = Rows(VideoUploadCard.BuildComponents([Video(0), Short(0), Video(1), Short(1), Video(2), Short(2)]));
        Assert.Equal(new[] { 4, 2 }, rows.Select(r => r.Count));
        Assert.Equal(new[] { "Upload #1", "Short #1", "Upload #2", "Short #2" }, rows[0].Select(b => b.Label));
        Assert.Equal(new[] { "Upload #3", "Short #3" }, rows[1].Select(b => b.Label));
    }

    [Fact]
    public void Ten_videos_with_shorts_fit_five_rows()
    {
        var rows = Enumerable.Range(0, 10).SelectMany(i => new[] { Video(i), Short(i) }).ToList();
        var built = Rows(VideoUploadCard.BuildComponents(rows));
        Assert.Equal(new[] { 4, 4, 4, 4, 4 }, built.Select(r => r.Count));
        Assert.Equal("Short #10", built[4][3].Label);
    }

    [Fact]
    public void Rows_without_shorts_keep_the_upload_only_layout()
    {
        var rows = Rows(VideoUploadCard.BuildComponents(Enumerable.Range(0, 7).Select(i => Video(i)).ToList()));
        Assert.Equal(new[] { 5, 2 }, rows.Select(r => r.Count));
        Assert.All(rows.SelectMany(r => r), b => Assert.StartsWith("vidup:", b.CustomId));
    }

    private static List<List<ButtonComponent>> Rows(MessageComponent c) =>
        c.Components.OfType<ActionRowComponent>()
            .Select(r => r.Components.OfType<ButtonComponent>().ToList())
            .ToList();

    private static List<ButtonComponent> Buttons(MessageComponent c) => Rows(c).SelectMany(r => r).ToList();

    [Fact]
    public void A_single_pending_video_gets_one_unnumbered_upload_button()
    {
        var b = Assert.Single(Buttons(VideoUploadCard.BuildComponents([Video(0)])));
        Assert.Equal("Upload", b.Label);
        Assert.Equal("vidup:0", b.CustomId);
        Assert.Equal(ButtonStyle.Primary, b.Style);
        Assert.False(b.IsDisabled);
    }

    [Fact]
    public void An_uploading_video_gets_a_disabled_button()
    {
        var b = Assert.Single(Buttons(VideoUploadCard.BuildComponents([Video(0, VideoSubmissionStatus.Uploading)])));
        Assert.Equal("Uploading…", b.Label);
        Assert.True(b.IsDisabled);
    }

    [Fact]
    public void An_uploaded_video_gets_a_watch_link()
    {
        var b = Assert.Single(Buttons(VideoUploadCard.BuildComponents([Video(0, VideoSubmissionStatus.Uploaded, "abc")])));
        Assert.Equal("Watch on YouTube", b.Label);
        Assert.Equal(ButtonStyle.Link, b.Style);
        Assert.Equal("https://youtu.be/abc", b.Url);
    }

    [Fact]
    public void A_video_whose_original_was_deleted_gets_a_disabled_original_deleted_button()
    {
        var b = Assert.Single(Buttons(VideoUploadCard.BuildComponents([Video(0, VideoSubmissionStatus.OriginalDeleted)])));
        Assert.Equal("Original deleted", b.Label);
        Assert.Equal(ButtonStyle.Secondary, b.Style);
        Assert.True(b.IsDisabled);
        Assert.Equal("vidup:0", b.CustomId);
    }

    [Fact]
    public void Several_videos_are_numbered_in_attachment_order()
    {
        // Attachment 2 is a screenshot (no row), and the rows arrive out of order.
        var buttons = Buttons(VideoUploadCard.BuildComponents([Video(3), Video(0), Video(1, VideoSubmissionStatus.OriginalDeleted)]));
        Assert.Equal(new[] { "Upload #1", "Original deleted #2", "Upload #3" }, buttons.Select(b => b.Label));
        Assert.Equal(new[] { "vidup:0", "vidup:1", "vidup:3" }, buttons.Select(b => b.CustomId));
    }

    [Fact]
    public void Ten_videos_fill_two_rows_of_five()
    {
        var rows = Rows(VideoUploadCard.BuildComponents(Enumerable.Range(0, 10).Select(i => Video(i)).ToList()));
        Assert.Equal(new[] { 5, 5 }, rows.Select(r => r.Count));
        Assert.Equal("Upload #10", rows[1][4].Label);
    }

    [Fact]
    public void The_text_names_the_poster_and_channel_and_links_the_post()
    {
        Assert.Equal(
            "🎬 Clip by <@42> in <#600> · [Jump to post](https://discord.com/channels/1/600/100)",
            VideoUploadCard.BuildText(Video(0), originalDeleted: false));
    }

    [Fact]
    public void The_text_gains_the_deleted_line_when_the_original_is_gone()
    {
        Assert.Equal(
            "🎬 Clip by <@42> in <#600> · [Jump to post](https://discord.com/channels/1/600/100)\n" +
            "🗑️ Original post deleted, so this clip can't be uploaded.",
            VideoUploadCard.BuildText(Video(0), originalDeleted: true));
    }
}
