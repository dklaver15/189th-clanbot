using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class ClipTimeTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(180, "3:00")]
    [InlineData(29.6, "0:30")]
    public void Seconds_are_formatted_as_mss(double seconds, string text) =>
        Assert.Equal(text, ClipTime.Format(seconds));
}
