using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Hosted service that manages the Discord client lifecycle —
/// connecting, registering event handlers, and registering slash commands.
/// </summary>
public class DiscordBotService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly ActivityTrackingHandler _activityHandler;
    private readonly GamertagCommandHandler _gamertagHandler;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly BotConfig _config;

    public DiscordBotService(
        DiscordSocketClient client,
        ActivityTrackingHandler activityHandler,
        GamertagCommandHandler gamertagHandler,
        ILogger<DiscordBotService> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _activityHandler = activityHandler;
        _gamertagHandler = gamertagHandler;
        _logger = logger;
        _config = config.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += LogAsync;
        _client.Ready += OnReadyAsync;

        // Register the activity tracking event handlers
        _activityHandler.Register(_client);
        
        // Register the gamertag command handler
        _gamertagHandler.Register(_client);

        await _client.LoginAsync(TokenType.Bot, _config.Token);
        await _client.StartAsync();

        _logger.LogInformation("Discord bot started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.StopAsync();
        _logger.LogInformation("Discord bot stopped");
    }

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Bot is connected as {BotUser} to {GuildCount} guild(s)",
            _client.CurrentUser, _client.Guilds.Count);

        try
        {
            // Register global slash commands
            var commands = new[]
            {
                new SlashCommandBuilder()
                    .WithName("clanguard-status")
                    .WithDescription("Show your current activity stats for this server")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("clanguard-check")
                    .WithDescription("Check another user's activity stats (Officer+ only)")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The user to check", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("clanguard-exempt")
                    .WithDescription("Remove a user's AWOL role and reset their window (Officer+ only)")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The user to exempt", isRequired: true)
                    .Build(),
                
                new SlashCommandBuilder()
                    .WithName("gamertags")
                    .WithDescription("Enter your gamertags for EA, Steam, PSN, Xbox, Embark, and Bungie")
                    .Build()
            };

            foreach (var command in commands)
            {
                await _client.CreateGlobalApplicationCommandAsync(command);
            }

            _logger.LogInformation("Slash commands registered");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register slash commands");
        }
    }

    private Task LogAsync(LogMessage message)
    {
        var severity = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            LogSeverity.Debug => LogLevel.Trace,
            _ => LogLevel.Information
        };

        _logger.Log(severity, message.Exception, "[Discord] {Source}: {Message}",
            message.Source, message.Message);

        return Task.CompletedTask;
    }
}
