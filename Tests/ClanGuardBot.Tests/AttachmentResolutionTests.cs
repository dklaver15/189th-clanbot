using ClanGuardBot.Handlers;

namespace ClanGuardBot.Tests;

/// <summary>
/// The Upload button stores the video's position in the member's post, and the
/// member can edit the post afterwards and remove attachments, which shifts the
/// positions. The stored file name is what proves it's still the same clip.
/// </summary>
public class AttachmentResolutionTests
{
    [Fact]
    public void An_unchanged_post_resolves_to_the_stored_position()
    {
        Assert.Equal(1, VideoRepostHandler.ResolveAttachmentIndex(["a.mp4", "b.mp4"], 1, "b.mp4"));
    }

    [Fact]
    public void A_clip_whose_earlier_neighbour_was_removed_is_found_by_name()
    {
        // The post was [a.mp4, b.mp4]; the member removed a.mp4, so b.mp4 moved to 0.
        Assert.Equal(0, VideoRepostHandler.ResolveAttachmentIndex(["b.mp4"], 1, "b.mp4"));
    }

    [Fact]
    public void A_clip_the_member_removed_resolves_to_nothing_instead_of_another_clip()
    {
        // The post was [a.mp4, b.mp4]; the member removed a.mp4. Upload #1 must not send b.mp4.
        Assert.Null(VideoRepostHandler.ResolveAttachmentIndex(["b.mp4"], 0, "a.mp4"));
    }

    [Fact]
    public void Two_clips_with_the_same_name_at_shifted_positions_are_not_guessed_between()
    {
        Assert.Null(VideoRepostHandler.ResolveAttachmentIndex(["clip.mp4", "clip.mp4"], 2, "clip.mp4"));
    }

    [Fact]
    public void Two_clips_with_the_same_name_still_resolve_when_the_stored_position_matches()
    {
        Assert.Equal(1, VideoRepostHandler.ResolveAttachmentIndex(["clip.mp4", "clip.mp4"], 1, "clip.mp4"));
    }
}
