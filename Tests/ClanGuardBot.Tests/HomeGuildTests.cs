using ClanGuardBot.Services;

namespace ClanGuardBot.Tests;

public class HomeGuildTests
{
    private sealed record Guild(ulong Id, string Name);

    private static readonly Guild Clan = new(1377419012685693088, "189th");
    private static readonly Guild Other = new(1111111111111111111, "STARBUCKS");

    private static Guild? Resolve(ulong configuredId, params Guild[] guilds) =>
        HomeGuild.Resolve(configuredId, guilds, g => g.Id);

    [Fact]
    public void ConfiguredIdWinsEvenWhenListedSecond() =>
        Assert.Same(Clan, Resolve(Clan.Id, Other, Clan));

    [Fact]
    public void ConfiguredIdNotConnectedIsNull() =>
        Assert.Null(Resolve(Clan.Id, Other));

    [Fact]
    public void UnsetWithOneServerUsesIt() =>
        Assert.Same(Clan, Resolve(0, Clan));

    [Fact]
    public void UnsetWithSeveralServersDoesNotGuess() =>
        Assert.Null(Resolve(0, Other, Clan));
}
