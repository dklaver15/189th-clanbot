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
    private readonly RankTrackingHandler _rankHandler;
    private readonly TicketReminderHandler _ticketReminderHandler;
    private readonly GuestReminderHandler _guestReminderHandler;
    private readonly OnboardingReminderHandler _onboardingReminderHandler;
    private readonly ApolloEventHandler _apolloEventHandler;
    private readonly CompEventCommandHandler _compEventHandler;
    private readonly PromoteCommandHandler _promoteHandler;
    private readonly DemoteCommandHandler _demoteHandler;
    private readonly SetNickCommandHandler _setNickHandler;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly BotConfig _config;

    public DiscordBotService(
        DiscordSocketClient client,
        ActivityTrackingHandler activityHandler,
        GamertagCommandHandler gamertagHandler,
        RankTrackingHandler rankHandler,
        TicketReminderHandler ticketReminderHandler,
        GuestReminderHandler guestReminderHandler,
        OnboardingReminderHandler onboardingReminderHandler,
        ApolloEventHandler apolloEventHandler,
        CompEventCommandHandler compEventHandler,
        PromoteCommandHandler promoteHandler,
        DemoteCommandHandler demoteHandler,
        SetNickCommandHandler setNickHandler,
        ILogger<DiscordBotService> logger,
        IOptions<BotConfig> config)
    {
        _client                    = client;
        _activityHandler           = activityHandler;
        _gamertagHandler           = gamertagHandler;
        _rankHandler               = rankHandler;
        _ticketReminderHandler     = ticketReminderHandler;
        _guestReminderHandler      = guestReminderHandler;
        _onboardingReminderHandler = onboardingReminderHandler;
        _apolloEventHandler        = apolloEventHandler;
        _compEventHandler          = compEventHandler;
        _promoteHandler            = promoteHandler;
        _demoteHandler             = demoteHandler;
        _setNickHandler            = setNickHandler;
        _logger                    = logger;
        _config                    = config.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log    += LogAsync;
        _client.Ready  += OnReadyAsync;

        _activityHandler.Register(_client);
        _gamertagHandler.Register(_client);
        _rankHandler.Register(_client);
        _ticketReminderHandler.Register(_client);
        _guestReminderHandler.Register(_client);
        _onboardingReminderHandler.Register(_client);
        _apolloEventHandler.Register(_client);
        _compEventHandler.Register(_client);
        _promoteHandler.Register(_client);
        _demoteHandler.Register(_client);
        _setNickHandler.Register(_client);

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
            // Clear any stale global commands
            await _client.BulkOverwriteGlobalApplicationCommandsAsync(
                Array.Empty<ApplicationCommandProperties>());

            var commands = new[]
            {
                new SlashCommandBuilder()
                    .WithName("awol-status")
                    .WithDescription("Show your current activity stats for this server")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("awol-check")
                    .WithDescription("Check another user's activity stats (Officer+ only)")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The user to check", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("awol-exempt")
                    .WithDescription("Remove a user's AWOL role and reset their window (Officer+ only)")
                    .AddOption("user", ApplicationCommandOptionType.User,
                        "The user to exempt", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("gamertags")
                    .WithDescription("Enter your gamertags for EA, Steam, PSN, Xbox, Embark, and Bungie")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("roster-export")
                    .WithDescription("Manually trigger a roster export to Google Sheets (Officer+ only)")
                    .Build(),

                // NOTE: /recruit was removed — new recruits are auto-logged by
                // RankTrackingHandler when they gain the RCT role.

                new SlashCommandBuilder()
                    .WithName("comp-event")
                    .WithDescription($"Create a competitive division event on the clan calendar ({_config.CompEventMinRank}+ only)")
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("promote")
                    .WithDescription("Promote a member to the next rank")
                    .AddOption("rank", ApplicationCommandOptionType.String,
                        "The rank to promote to (e.g. pvt, pfc, sgt)", isRequired: true)
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to promote", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("demote")
                    .WithDescription("Demote a member to a lower rank")
                    .AddOption("rank", ApplicationCommandOptionType.String,
                        "The rank to demote to (e.g. rct, pvt, pfc)", isRequired: true)
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to demote", isRequired: true)
                    .Build(),

                new SlashCommandBuilder()
                    .WithName("setnick")
                    .WithDescription("Change a member's nickname (Officer+ only)")
                    .AddOption("member", ApplicationCommandOptionType.User,
                        "The member to rename", isRequired: true)
                    .AddOption("nickname", ApplicationCommandOptionType.String,
                        "The new nickname", isRequired: true)
                    .Build()
            };

            foreach (var guild in _client.Guilds)
            {
                await guild.BulkOverwriteApplicationCommandAsync(commands);
                _logger.LogInformation("Slash commands registered to guild {GuildName}", guild.Name);
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
            LogSeverity.Error    => LogLevel.Error,
            LogSeverity.Warning  => LogLevel.Warning,
            LogSeverity.Info     => LogLevel.Information,
            LogSeverity.Verbose  => LogLevel.Debug,
            LogSeverity.Debug    => LogLevel.Trace,
            _                    => LogLevel.Information
        };

        _logger.Log(severity, message.Exception, "[Discord] {Source}: {Message}",
            message.Source, message.Message);

        return Task.CompletedTask;
    }
}