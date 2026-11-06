using ClanGuardBot.Handlers;
using ClanGuardBot.Services;
using Discord;

namespace ClanGuardBot.Tests;

public class RedsecOperatorListTests
{
    private const int Top = 25;

    private static string WorstCaseList() => RedsecCommandHandler.FormatOperatorList(
        Enumerable.Range(1, Top).Select(i => new RedsecOperatorLine(
            i, new RedsecSquadMember(ulong.MaxValue, "name", null), 999, 99_999)),
        showEngagements: true);

    [Fact]
    public void A_full_list_is_too_long_for_an_embed_field()
    {
        Assert.True(WorstCaseList().Length > EmbedFieldBuilder.MaxFieldValueLength);
    }

    [Fact]
    public void A_full_list_fits_in_the_embed_description()
    {
        var embed = new EmbedBuilder().WithDescription("**999** wins by **999** operators.");
        embed.Description += "\n\n**Top operators**\n" + WorstCaseList();

        var built = embed.Build();

        Assert.Equal(Top, built.Description.Split("**#").Length - 1);
    }
}
