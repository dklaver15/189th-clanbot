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
            // Enabled=false should boot fine even without secrets in place.
            .Validate(o =>
                !o.Enabled
                || (!string.IsNullOrWhiteSpace(o.ClientId)
                    && !string.IsNullOrWhiteSpace(o.ClientSecret)
                    && !string.IsNullOrWhiteSpace(o.Username)
                    && !string.IsNullOrWhiteSpace(o.Password)
                    && o.LeadsChannelId != 0
                    && !string.IsNullOrWhiteSpace(o.Subreddits)),
                "RedditLeads is enabled but ClientId / ClientSecret / Username / Password / LeadsChannelId / Subreddits must all be set")
            .ValidateOnStart();

        services.AddHttpClient<RedditApiClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<RedditLeadsOptions>>().Value;
            // Reddit's rules require a descriptive User-Agent; missing or
            // generic UAs (curl/python-requests/etc.) get aggressively
            // rate-limited or blocked. We set it once on the typed client
            // instead of per-request so token requests inherit it too.
            if (!string.IsNullOrWhiteSpace(opts.UserAgent))
                client.DefaultRequestHeaders.UserAgent.ParseAdd(opts.UserAgent);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

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
