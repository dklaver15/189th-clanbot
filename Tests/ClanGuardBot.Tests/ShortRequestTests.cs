using ClanGuardBot.Models;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class ShortRequestTests
{
    private static ShortSettings Ok(string start, string? end, string title = "Clip")
    {
        Assert.True(ShortRequest.TryParse(start, end, title, ShortLayout.FullViewZoom, out var s, out var error), error);
        return s;
    }

    private static string Fails(string start, string? end, string title = "Clip")
    {
        Assert.False(ShortRequest.TryParse(start, end, title, ShortLayout.FullViewZoom, out _, out var error));
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
    public void An_empty_end_means_the_end_of_the_clip()
    {
        var s = Ok("0:00", "  ", "Nice shot");
        Assert.Null(s.EndSeconds);
        Assert.Equal("Nice shot", s.Title);
        Assert.Equal(ShortLayout.FullViewZoom, s.Layout);
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
    public void Longer_than_three_minutes_is_refused() =>
        Assert.Equal("A Short can be at most 3:00 long, and 0:00 to 3:01 is 3:01.", Fails("0:00", "3:01"));

    [Fact]
    public void Exactly_three_minutes_is_allowed() =>
        Assert.Equal(180, Ok("0:00", "3:00").EndSeconds);

    [Theory]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1:75")]
    [InlineData("1:5")]
    [InlineData("")]
    public void An_unreadable_start_is_refused(string start) =>
        Assert.Equal("Start must be a time like 0:05 or a number of seconds, like 5.", Fails(start, null));

    [Fact]
    public void An_unreadable_end_is_refused() =>
        Assert.Equal("End must be a time like 0:30, a number of seconds, or empty for the end of the clip.", Fails("0:00", "soon"));

    [Fact]
    public void An_empty_title_is_refused() =>
        Assert.Equal("Title can't be empty.", Fails("0:00", null, "   "));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(180, "3:00")]
    [InlineData(29.6, "0:30")]
    public void Times_are_formatted_as_mss(double seconds, string text) =>
        Assert.Equal(text, ShortRequest.FormatTime(seconds));
}
