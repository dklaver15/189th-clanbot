using System.Text.RegularExpressions;
using ClanGuardBot.Models;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class ShortRendererFilterTests
{
    private static ShortRenderer.Frame Zoom(int w, int h) => ShortRenderer.BuildFilter(ShortLayout.FullViewZoom, w, h);

    private static (int W, int H) Crop(string filter)
    {
        var m = Regex.Match(filter, @"crop=(\d+):(\d+)");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    public void Sixteen_by_nine_zoom_splits_608_over_1312(int w, int h)
    {
        var f = Zoom(w, h);
        Assert.Equal((1080, 1920), (f.Width, f.Height));
        Assert.Contains("scale=1080:608[top]", f.Filter);
        Assert.Contains("scale=1080:1312[bottom]", f.Filter);
    }

    [Fact]
    public void Ultrawide_zoom_still_fills_1920()
    {
        var f = Zoom(2560, 1080);
        Assert.Contains("scale=1080:456[top]", f.Filter);
        Assert.Contains("scale=1080:1464[bottom]", f.Filter);
    }

    [Theory]
    [InlineData(2560, 1440)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1080)]
    [InlineData(1100, 1080)]
    public void Near_square_landscape_zoom_crop_stays_inside_the_source(int w, int h)
    {
        var (cw, ch) = Crop(Zoom(w, h).Filter);
        Assert.InRange(cw, 2, w);
        Assert.InRange(ch, 2, h);
        Assert.Equal(0, cw % 2);
        Assert.Equal(0, ch % 2);
    }

    [Fact]
    public void Center_crop_takes_a_centred_9_16_slice()
    {
        var f = ShortRenderer.BuildFilter(ShortLayout.CenterCrop, 2560, 1440);
        Assert.Equal("crop=810:1440,scale=1080:1920,setsar=1", f.Filter);
        Assert.Equal((1080, 1920), (f.Width, f.Height));
    }

    [Theory]
    [InlineData(1080, 1920, 1080, 1920)]
    [InlineData(1080, 1080, 1080, 1080)]
    [InlineData(720, 1280, 1080, 1920)]
    public void Vertical_and_square_clips_keep_their_shape(int w, int h, int outW, int outH)
    {
        foreach (var layout in Enum.GetValues<ShortLayout>())
        {
            var f = ShortRenderer.BuildFilter(layout, w, h);
            Assert.Equal((outW, outH), (f.Width, f.Height));
            Assert.Equal($"scale={outW}:{outH},setsar=1", f.Filter);
        }
    }

    [Fact]
    public void Taller_than_9_16_fits_by_height()
    {
        var f = ShortRenderer.BuildFilter(ShortLayout.AsIs, 720, 1600);
        Assert.Equal((864, 1920), (f.Width, f.Height));
    }

    [Fact]
    public void AsIs_on_a_landscape_source_renders_like_full_view_zoom() =>
        Assert.Equal(Zoom(1920, 1080), ShortRenderer.BuildFilter(ShortLayout.AsIs, 1920, 1080));
}
