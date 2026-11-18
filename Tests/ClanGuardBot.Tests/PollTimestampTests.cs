using ClanGuardBot.Models;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class PollTimestampTests
{
    // Sat, Nov 21, 2026 01:00 UTC = Fri, Nov 20, 2026 8:00 PM EST.
    private const long FridayEightPm = 1795222800;
    // Sat, Jul 4, 2026 00:30 UTC = Fri, Jul 3, 2026 8:30 PM EDT.
    private const long JulyThirdEightThirty = 1783125000;
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("t", "8:00 PM ET")]
    [InlineData("T", "8:00:00 PM ET")]
    [InlineData("d", "Fri, Nov 20")]
    [InlineData("D", "Fri, Nov 20")]
    [InlineData("f", "Fri, Nov 20, 8:00 PM ET")]
    [InlineData("F", "Fri, Nov 20, 8:00 PM ET")]
    [InlineData("s", "Fri, Nov 20, 8:00 PM ET")]
    [InlineData("S", "Fri, Nov 20, 8:00 PM ET")]
    [InlineData("R", "Fri, Nov 20, 8:00 PM ET")]
    public void Each_style_becomes_eastern_text(string style, string expected) =>
        Assert.Equal($"Raid {expected}?", PollTimestampResolver.Resolve($"Raid <t:{FridayEightPm}:{style}>?", Now));

    [Fact]
    public void No_style_reads_like_the_default_f() =>
        Assert.Equal("Fri, Nov 20, 8:00 PM ET", PollTimestampResolver.Resolve($"<t:{FridayEightPm}>", Now));

    [Fact]
    public void Daylight_time_is_handled() =>
        Assert.Equal("Fri, Jul 3, 8:30 PM ET", PollTimestampResolver.Resolve($"<t:{JulyThirdEightThirty}:F>", Now));

    [Fact]
    public void A_different_year_is_shown() =>
        Assert.Equal("Fri, Nov 20, 2026, 8:00 PM ET",
            PollTimestampResolver.Resolve($"<t:{FridayEightPm}:F>", new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No times here <#123456789012345678>")]
    [InlineData("<t:abc:F> <t:123:X>")]
    public void Text_without_valid_tokens_is_untouched(string? text) =>
        Assert.Equal(text, PollTimestampResolver.Resolve(text, Now));

    [Fact]
    public void Out_of_range_tokens_are_left_alone() =>
        Assert.Equal("<t:9999999999999:F>", PollTimestampResolver.Resolve("<t:9999999999999:F>", Now));

    [Fact]
    public void Longest_fallback_fits_a_native_answer() =>
        Assert.True(PollTimestampResolver.Format(FridayEightPm, 'F', new DateTime(2025, 1, 1)).Length <= 40);

    [Fact]
    public void Local_time_lines_pair_eastern_text_with_a_real_token_once_each()
    {
        var lines = PollTimestampResolver.LocalTimeLines(
            [$"Raid at <t:{FridayEightPm}:F>?", $"Yes <t:{FridayEightPm}:F>", $"Earlier <t:{FridayEightPm}:t>", "No", null], Now);

        Assert.Equal(
        [
            $"Fri, Nov 20, 8:00 PM ET → <t:{FridayEightPm}:F> (<t:{FridayEightPm}:R>)",
            $"8:00 PM ET → <t:{FridayEightPm}:F> (<t:{FridayEightPm}:R>)",
        ], lines);
    }

    [Fact]
    public void Local_times_go_under_the_description()
    {
        var result = PollPublisher.AppendLocalTimes("Bring ammo.", ["A", "B"]);
        Assert.Equal("Bring ammo.\n\n🕒 **In your time zone:**\nA\nB", result);
    }

    [Fact]
    public void Local_times_stand_alone_without_a_description() =>
        Assert.Equal("🕒 **In your time zone:**\nA", PollPublisher.AppendLocalTimes(null, ["A"]));

    [Fact]
    public void Lines_that_do_not_fit_are_dropped_whole()
    {
        var description = new string('x', PollPublisher.MaxDescriptionLength - 40);
        var result = PollPublisher.AppendLocalTimes(description, ["short", new string('y', 50)])!;

        Assert.EndsWith("\nshort", result);
        Assert.True(result.Length <= PollPublisher.MaxDescriptionLength);
    }

    [Fact]
    public void Nothing_is_added_when_no_line_fits()
    {
        var description = new string('x', PollPublisher.MaxDescriptionLength);
        Assert.Same(description, PollPublisher.AppendLocalTimes(description, ["A"]));
    }

    [Fact]
    public void Anonymous_question_is_a_heading_and_buttons_get_eastern_text()
    {
        var poll = new Poll { Id = 1, Question = $"Raid <t:{FridayEightPm}:F>?", Status = PollStatus.Open, ClosesAtUtc = Now };
        var options = new List<PollOption> { new() { Id = 7, Position = 0, Label = $"<t:{FridayEightPm}:t>" } };

        var embed = PollEmbedBuilder.BuildEmbed(poll, options, new Dictionary<int, int>(), 0, null, creditInFooter: false);
        Assert.True(string.IsNullOrEmpty(embed.Title));
        Assert.StartsWith($"### 📊 Raid <t:{FridayEightPm}:F>?\n\n", embed.Description);
        Assert.Contains($"**<t:{FridayEightPm}:t>**", embed.Description);

        var button = PollEmbedBuilder.BuildComponents(poll, options, locked: false)
            .Components.OfType<Discord.ActionRowComponent>().First().Components.OfType<Discord.ButtonComponent>().First();
        Assert.EndsWith(" PM ET", button.Label);
    }
}
