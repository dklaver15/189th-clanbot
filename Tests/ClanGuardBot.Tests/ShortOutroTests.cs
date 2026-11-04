using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

/// <summary>Where the 1080x1920 outro is cropped for each Short frame size.</summary>
public class ShortOutroTests
{
    [Theory]
    [InlineData(1080, 1920,   0,   0)]   // every landscape layout
    [InlineData(1080, 1080,   0, 520)]   // square: moved up from 420 so the pill fits
    [InlineData(1080, 1200,   0, 400)]   // near square: moved up from 360
    [InlineData(1080, 1350,   0, 285)]   // 4:5: centred already fits
    [InlineData( 864, 1920, 108,   0)]   // taller than 9:16
    public void The_outro_crop_keeps_the_logo_and_pill_in_view(int w, int h, int x, int y)
    {
        Assert.Equal((x, y), ShortOutro.CropOrigin(w, h));
        Assert.InRange(ShortOutro.KeepBandTop, y, y + h);
        Assert.InRange(ShortOutro.KeepBandBottom, y, y + h);
    }
}
