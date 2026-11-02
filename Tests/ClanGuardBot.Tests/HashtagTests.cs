using ClanGuardBot.Models;
using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

/// <summary>The Hashtags box on the upload form: what the uploader types, cleaned.</summary>
public class HashtagTests
{
    [Theory]
    [InlineData("#Battlefield #clutch", new[] { "Battlefield", "clutch" })]
    [InlineData("Battlefield, clutch,,  sniper", new[] { "Battlefield", "clutch", "sniper" })]
    [InlineData("#Battle-field! #😀 # ##", new[] { "Battlefield" })]
    [InlineData("#Clutch #clutch #CLUTCH", new[] { "Clutch" })]
    [InlineData("#Überclutch #189th_clan", new[] { "Überclutch", "189th_clan" })]
    [InlineData(null, new string[0])]
    [InlineData("   ", new string[0])]
    public void Typed_hashtags_are_cleaned(string? typed, string[] expected) =>
        Assert.Equal(expected, Hashtags.Parse(typed));

    [Fact]
    public void At_most_fifteen_are_kept_in_order() =>
        Assert.Equal(Enumerable.Range(1, 15).Select(i => $"t{i}"),
                     Hashtags.Parse(string.Join(' ', Enumerable.Range(1, 20).Select(i => $"#t{i}"))));

    [Fact]
    public void Format_puts_a_hash_on_each() =>
        Assert.Equal("#Battlefield #clutch", Hashtags.Format(["Battlefield", "clutch"]));

    [Fact]
    public void Shorts_is_added_once_and_last() =>
        Assert.Equal(new[] { "Battlefield", "clutch", "Shorts" }, Hashtags.WithShorts(Hashtags.Parse("#shorts #Battlefield #SHORTS #clutch")));

    [Fact]
    public void The_battlefield_and_wardogs_channels_prefill_their_tag()
    {
        var map = new BotConfig().VideoHashtagsByChannel;
        Assert.Equal("#Battlefield", Hashtags.DefaultFor(map, 1408199889879564539));
        Assert.Equal("#WARDOGS",     Hashtags.DefaultFor(map, 1549891740993982504));
        Assert.Null(Hashtags.DefaultFor(map, 1551837069632995378));
        Assert.Null(Hashtags.DefaultFor(map, null));
    }
}
