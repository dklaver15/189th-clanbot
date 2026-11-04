using System.Text;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

/// <summary>What goes into a YouTube upload's description and tags.</summary>
public class YouTubeDescriptionTests
{
    [Fact]
    public void Hashtags_go_on_their_own_last_line() =>
        Assert.Equal("Nice shot\n\nClip by Syrup.\n\n#Battlefield #Shorts",
            YouTubeUploadService.ComposeDescription("Nice shot\n\nClip by Syrup.", ["Battlefield", "Shorts"]));

    [Fact]
    public void No_hashtags_means_no_extra_line() =>
        Assert.Equal("Clip by Syrup.", YouTubeUploadService.ComposeDescription(" Clip by <Syrup>. ", []));

    [Fact]
    public void A_long_description_is_cut_above_the_hashtags()
    {
        var d = YouTubeUploadService.ComposeDescription(new string('é', 4000), ["Battlefield", "Shorts"]);
        Assert.InRange(Encoding.UTF8.GetByteCount(d), 1, 5000);
        Assert.EndsWith("\n\n#Battlefield #Shorts", d);
    }

    [Fact]
    public void Tags_stop_before_500_characters()
    {
        var tags = YouTubeUploadService.BuildTags(Enumerable.Range(0, 20).Select(i => $"{i:00}" + new string('a', 38)).ToList());
        Assert.Equal(12, tags.Count);                       // 12×40 + 11 commas = 491; a 13th would be 532
        Assert.True(tags.Sum(t => t.Length) + tags.Count - 1 <= 500);
    }
}
