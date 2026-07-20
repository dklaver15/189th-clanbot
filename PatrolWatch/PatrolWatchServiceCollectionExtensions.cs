using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClanGuardBot.PatrolWatch;

/// <summary>
/// Wires up the Patrol Watch subsystem: options binding + validation, the
/// hosted background service, and the /patrol slash-command handler.
///
/// Mirrors the AddRedditLeads pattern so all feature-flagged subsystems
/// register the same way from Program.cs.
/// </summary>
public static class PatrolWatchServiceCollectionExtensions
{
    public static IServiceCollection AddPatrolWatch(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<PatrolWatchOptions>()
            .Bind(configuration.GetSection(PatrolWatchOptions.Section))
            // Only validate the required fields when the feature is enabled.
            // A clean install with Enabled=false should boot fine even with
            // an empty PatrolWatch section.
            .Validate(o =>
                    !o.Enabled
                    || (o.LfgChannelId != 0
                        && o.MinSquadSize >= 2
                        && o.MatchedGames.Count > 0
                        && o.MatchedGames.All(g =>
                            !string.IsNullOrWhiteSpace(g.DisplayName)
                            && g.ActivitySubstrings.Count > 0)),
                "PatrolWatch is enabled but LfgChannelId / MinSquadSize / MatchedGames must all be set, with at least one ActivitySubstring per game")
            .ValidateOnStart();

        // The watcher itself. Hosted so it starts/stops with the bot.
        services.AddHostedService<PatrolWatchService>();

        // Slash-command handlers — singletons so DiscordBotService.Register can
        // call .Register(client). Same pattern as RedditLeadsCommandHandler.
        services.AddSingleton<PatrolWatchCommandHandler>();
        services.AddSingleton<PatrolNameCommandHandler>();

        return services;
    }
}