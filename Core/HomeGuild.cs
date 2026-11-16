using ClanGuardBot.Models;
using Discord.WebSocket;

namespace ClanGuardBot.Services;

public static class HomeGuild
{
    /// <summary>
    /// The clan's server: <see cref="BotConfig.GuildId"/> when set, otherwise the
    /// only server the bot is in. Null when it can't be told apart, so callers
    /// skip rather than run against the wrong server.
    /// </summary>
    public static SocketGuild? GetHomeGuild(this DiscordSocketClient client, BotConfig config) =>
        Resolve(config.GuildId, client.Guilds, g => g.Id);

    /// <summary>The clan's server as a list of zero or one, for jobs that loop over servers.</summary>
    public static IReadOnlyList<SocketGuild> HomeGuildOnly(this DiscordSocketClient client, BotConfig config) =>
        client.GetHomeGuild(config) is { } guild ? [guild] : [];

    internal static T? Resolve<T>(ulong configuredId, IReadOnlyCollection<T> guilds, Func<T, ulong> idOf)
        where T : class =>
        configuredId != 0
            ? guilds.FirstOrDefault(g => idOf(g) == configuredId)
            : guilds.Count == 1 ? guilds.First() : null;
}
