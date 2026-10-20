using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

/// <summary>
/// How much of the clip a Short renders. A render of a full 3:00 can come out a
/// few ms over once muxed, and YouTube then files it as a normal video, so a Short
/// stops at 179.5 s.
/// </summary>
public class ShortRenderLengthTests
{
    [Theory]
    [InlineData(0.0, null, 300.0, 179.5)]   // End empty on a long clip
    [InlineData(0.0, 180.0, 300.0, 179.5)]  // End typed as exactly 3:00
    [InlineData(0.0, null, 0.0, 179.5)]     // clip length unknown
    [InlineData(10.0, 40.0, 300.0, 30.0)]   // a normal trim
    [InlineData(10.0, null, 25.0, 15.0)]    // End empty: stops at the clip's end
    [InlineData(5.0, 100.0, 60.0, 55.0)]    // End past the clip's end
    public void A_short_renders_up_to_its_end_but_never_past_179_5_seconds(
        double start, double? end, double duration, double expected) =>
        Assert.Equal(expected, ShortRenderer.RenderSeconds(start, end, duration), precision: 6);
}
