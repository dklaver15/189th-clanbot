using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Wires up the Reddit lead pipeline: options binding, the typed Reddit
/// HttpClient, the polling background service, the button handler, and
/// the /leads slash-command surface.
///
/// Mirrors the BriefingServiceCollectionExtensions pattern so all
/// feature-flagged subsystems register the same way from Program.cs.
/// </summary>
public static class RedditLeadsServiceCollectionExtensions
{
    public static IServiceCollection AddRedditLeads(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<RedditLeadsOptions>()
            .Bind(configuration.GetSection(RedditLeadsOptions.Section))
            // Validate only when the feature is enabled. A clean install with
            // Enabled=false should boot fine even with empty fields.
            .Validate(o =>
                !o.Enabled
                || (o.LeadsChannelId != 0
                    && !string.IsNullOrWhiteSpace(o.Subreddits)
                    && !string.IsNullOrWhiteSpace(o.UserAgent)),
                "RedditLeads is enabled but LeadsChannelId / Subreddits / UserAgent must all be set")
            .ValidateOnStart();

        services.AddHttpClient<RedditRssClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<RedditLeadsOptions>>().Value;
            // Reddit's edge blocks generic UAs (curl, python-requests, empty).
            // Set once on the typed client so every request out of
            // RedditRssClient inherits it.
            if (!string.IsNullOrWhiteSpace(opts.UserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(opts.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // LeadMatcher: singleton because its per-sub regex dictionary is
        // built once at startup from RedditLeads + PatrolWatch config and
        // is immutable thereafter. Reads PatrolWatch.MatchedGames for the
        // game-alias vocabulary; IOptions<PatrolWatchOptions> resolves
        // even if AddPatrolWatch hasn't been called (returns a default-
        // constructed instance), so registration order in Program.cs
        // doesn't matter.
        services.AddSingleton<LeadMatcher>();

        // The polling loop. No singleton+hosted pair needed — nothing else
        // in the bot injects RedditLeadService; the slash command queries
        // the DB directly.
        services.AddHostedService<RedditLeadService>();

        // Singletons consumed by DiscordBotService.Register and by the
        // slash-command list in OnReadyAsync.
        services.AddSingleton<RedditLeadButtonHandler>();
        services.AddSingleton<RedditLeadsCommandHandler>();

        return services;
    }
}