using ClanGuardBot.Handlers;
using ClanGuardBot.Models;

namespace ClanGuardBot.Tests;

public class UploadIdsTests
{
    [Theory]
    [InlineData(VideoSubmissionKind.Video, ShortLayout.FullViewZoom)]
    [InlineData(VideoSubmissionKind.Short, ShortLayout.FullViewZoom)]
    [InlineData(VideoSubmissionKind.Short, ShortLayout.CenterCrop)]
    [InlineData(VideoSubmissionKind.Short, ShortLayout.AsIs)]
    public void Ids_round_trip_and_fit_discords_limit(VideoSubmissionKind kind, ShortLayout layout)
    {
        var id = VideoRepostHandler.BuildUploadId(VideoRepostHandler.FormPrefix, ulong.MaxValue, ulong.MaxValue - 1, 9, kind, layout);

        Assert.True(id.Length <= 100);
        Assert.True(VideoRepostHandler.TryParseUploadId(id, VideoRepostHandler.FormPrefix,
            out var channelId, out var messageId, out var index, out var parsedKind, out var parsedLayout));
        Assert.Equal((ulong.MaxValue, ulong.MaxValue - 1, 9, kind), (channelId, messageId, index, parsedKind));
        if (kind == VideoSubmissionKind.Short) Assert.Equal(layout, parsedLayout);
    }

    [Theory]
    [InlineData("vidupf:1:2:3")]
    [InlineData("vidupf:1:2:x:z")]
    [InlineData("vidupf:1:2:3:q")]
    public void Malformed_ids_are_rejected(string id) =>
        Assert.False(VideoRepostHandler.TryParseUploadId(id, VideoRepostHandler.FormPrefix, out _, out _, out _, out _, out _));

    [Fact]
    public void A_choice_id_is_not_a_form_id()
    {
        var id = VideoRepostHandler.BuildUploadId(VideoRepostHandler.ChoicePrefix, 1, 2, 3, VideoSubmissionKind.Video, ShortLayout.FullViewZoom);
        Assert.False(VideoRepostHandler.TryParseUploadId(id, VideoRepostHandler.FormPrefix, out _, out _, out _, out _, out _));
    }
}
