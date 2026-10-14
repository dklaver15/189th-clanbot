using ClanGuardBot.Handlers;
using ClanGuardBot.Models;

namespace ClanGuardBot.Tests;

/// <summary>The title pre-filled in the upload form (100 characters at most).</summary>
public class UploadTitleTests
{
    private static VideoSubmission Clip(string content, int index = 0) => new()
    {
        OriginalContent = content, PosterName = "Syrup", AttachmentIndex = index,
        PostedUtc = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void The_first_line_of_the_post_is_the_title()
    {
        Assert.Equal("Nice shot", VideoRepostHandler.BuildTitle(Clip("Nice shot\nsecond line"), 1));
    }

    [Fact]
    public void A_bare_post_is_titled_with_the_poster_and_date()
    {
        Assert.Equal("Syrup — Oct 14, 2026", VideoRepostHandler.BuildTitle(Clip(""), 1));
    }

    [Fact]
    public void A_post_with_several_videos_numbers_each_title()
    {
        Assert.Equal("Nice shot (part 2)", VideoRepostHandler.BuildTitle(Clip("Nice shot", index: 1), 2));
    }

    [Fact]
    public void A_title_of_exactly_100_characters_is_left_alone()
    {
        var text = new string('a', 100);
        Assert.Equal(text, VideoRepostHandler.BuildTitle(Clip(text), 1));
    }

    [Fact]
    public void A_numbered_title_of_exactly_100_characters_is_left_alone()
    {
        var title = VideoRepostHandler.BuildTitle(Clip(new string('a', 91)), 2);
        Assert.Equal(new string('a', 91) + " (part 1)", title);
    }

    [Fact]
    public void A_numbered_title_one_character_over_is_shortened_to_100()
    {
        var title = VideoRepostHandler.BuildTitle(Clip(new string('a', 92)), 2);
        Assert.Equal(new string('a', 90) + "… (part 1)", title);
    }

    [Fact]
    public void A_long_first_line_is_shortened_but_keeps_the_part_number()
    {
        var title = VideoRepostHandler.BuildTitle(Clip(new string('a', 150)), 2);

        Assert.Equal(new string('a', 90) + "… (part 1)", title);
        Assert.Equal(100, title.Length);
    }

    [Fact]
    public void An_emoji_that_fits_is_kept_whole()
    {
        var title = VideoRepostHandler.BuildTitle(Clip(new string('a', 97) + "😀" + "bbbb"), 1);
        Assert.Equal(new string('a', 97) + "😀…", title);
    }

    [Fact]
    public void An_emoji_on_the_cut_is_dropped_whole_instead_of_split()
    {
        var title = VideoRepostHandler.BuildTitle(Clip(new string('a', 98) + "😀" + "bbbb"), 1);
        Assert.Equal(new string('a', 98) + "…", title);
    }
}
