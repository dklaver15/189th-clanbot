using ClanGuardBot.Models;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class UploadRequestTests
{
    private static UploadSettings Ok(string start, string? end, string title = "Clip",
        VideoSubmissionKind kind = VideoSubmissionKind.Short)
    {
        Assert.True(UploadRequest.TryParse(start, end, title, kind, ShortLayout.CenterCrop, out var s, out var error), error);
        return s;
    }

    private static string Fails(string start, string? end, string title = "Clip",
        VideoSubmissionKind kind = VideoSubmissionKind.Short)
    {
        Assert.False(UploadRequest.TryParse(start, end, title, kind, ShortLayout.CenterCrop, out _, out var error));
        return error;
    }

    [Theory]
    [InlineData("0:05", 5)]
    [InlineData("1:05", 65)]
    [InlineData("12:30", 750)]
    [InlineData("65", 65)]
    [InlineData(" 0:07 ", 7)]
    public void Times_are_read_as_mss_or_seconds(string text, double seconds) =>
        Assert.Equal(seconds, Ok(text, null).StartSeconds);

    [Fact]
    public void An_empty_end_means_the_end_of_the_clip_and_the_choice_is_kept()
    {
        var s = Ok("0:00", "  ", "Nice shot");
        Assert.Null(s.EndSeconds);
        Assert.Equal("Nice shot", s.Title);
        Assert.Equal(VideoSubmissionKind.Short, s.Kind);
        Assert.Equal(ShortLayout.CenterCrop, s.Layout);
    }

    [Fact]
    public void Start_and_end_are_kept() =>
        Assert.Equal(15, Ok("0:05", "0:15").EndSeconds);

    [Fact]
    public void End_before_start_is_refused() =>
        Assert.Equal("End (0:05) must be after Start (0:10).", Fails("0:10", "0:05"));

    [Fact]
    public void End_equal_to_start_is_refused() =>
        Assert.Equal("End (0:10) must be after Start (0:10).", Fails("10", "0:10"));

    [Fact]
    public void A_short_longer_than_three_minutes_is_refused() =>
        Assert.Equal("A Short can be at most 3:00 long, and 0:00 to 3:01 is 3:01.", Fails("0:00", "3:01"));

    [Fact]
    public void A_short_of_exactly_three_minutes_is_allowed() =>
        Assert.Equal(180, Ok("0:00", "3:00").EndSeconds);

    [Fact]
    public void A_video_can_run_past_three_minutes() =>
        Assert.Equal(600, Ok("0:00", "10:00", kind: VideoSubmissionKind.Video).EndSeconds);

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:75")]
    [InlineData("1:5")]
    [InlineData("")]
    public void An_unreadable_start_is_refused(string start) =>
        Assert.Equal("Start must be a time like 0:05 or a number of seconds, like 5.", Fails(start, null));

    [Fact]
    public void Full_width_digits_get_the_field_message_instead_of_an_error() =>
        Assert.Equal("Start must be a time like 0:05 or a number of seconds, like 5.", Fails("０:０５", null));

    [Fact]
    public void An_unreadable_end_is_refused() =>
        Assert.Equal("End must be a time like 0:30, a number of seconds, or empty for the end of the clip.", Fails("0:00", "soon"));

    [Fact]
    public void An_empty_title_is_refused() =>
        Assert.Equal("Title can't be empty.", Fails("0:00", null, "   "));

    [Theory]
    [InlineData("0:00", null, false)]
    [InlineData("0:05", null, true)]
    [InlineData("0:00", "0:10", true)]
    public void Only_a_changed_start_or_end_counts_as_trimmed(string start, string? end, bool trimmed) =>
        Assert.Equal(trimmed, Ok(start, end).IsTrimmed);
}
