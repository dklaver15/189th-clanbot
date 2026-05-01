using ClanGuardBot.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Briefing;

public static class BriefingServiceCollectionExtensions
{
    /// <summary>
    /// Wires up Claude + the weekly briefing background service.
    /// Expects "Claude" and "WeeklyBriefing" sections in configuration.
    /// </summary>
    public static IServiceCollection AddWeeklyOfficerBriefing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ClaudeOptions>()
            .Bind(configuration.GetSection("Claude"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.ApiKey), "Claude:ApiKey is required")
            .ValidateOnStart();

        services
            .AddOptions<WeeklyBriefingOptions>()
            .Bind(configuration.GetSection("WeeklyBriefing"))
            .Validate(o => o.OfficerChannelId != 0, "WeeklyBriefing:OfficerChannelId is required")
            .ValidateOnStart();

        services.AddHttpClient<IAiService, ClaudeAiService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.DefaultRequestHeaders.Add("x-api-key", opts.ApiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", opts.ApiVersion);
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        // Optional: add Microsoft.Extensions.Http.Resilience and call .AddStandardResilienceHandler()
        // on the builder above for retries/circuit breaker. Not required at clan scale.

        services.AddSingleton<IBriefingDataCollector, BriefingDataCollector>();
        services.AddSingleton<WeeklyOfficerBriefingService>();
        services.AddHostedService(sp => sp.GetRequiredService<WeeklyOfficerBriefingService>());
        // Singleton+HostedService pattern lets you also inject the service into a slash
        // command handler for /briefing-now testing.

        return services;
    }
}