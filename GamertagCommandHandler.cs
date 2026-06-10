using Discord;
using Discord.WebSocket;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Entry points for the gamertag DM wizard. Two ways in, both of which hand off
/// to <see cref="GamertagWizard"/>:
///
///   • /gamertags slash command           → opens the wizard in the member's DMs
///   • "Enter Gamertags" button click      → same (CustomId <see cref="OpenButtonId"/>)
///
/// The persistent button is posted by GamertagSetupCommandHandler
/// ("/setup-gamertags"); its CustomId is exposed here as a public const so the
/// setup handler can reference it without a circular dependency. Keeping the
/// button working means the instructions message posted in past runs still
/// routes correctly — clicking it now launches the same DM wizard the slash
/// command does.
///
/// This handler owns only the entry interactions; all session state, prompts,
/// validation and the sheet write live on <see cref="GamertagWizard"/> (the same
/// split as EventCommandHandler ↔ EventCreationWizard).
/// </summary>
public sealed class GamertagCommandHandler
{
    public const string CommandName = "gamertags";

    /// <summary>
    /// CustomId for the persistent "Enter Gamertags" button posted by
    /// GamertagSetupCommandHandler. Stable across restarts so messages posted in
    /// past runs continue to route correctly.
    /// </summary>
    public const string OpenButtonId = "gamertags:open";

    private readonly GamertagWizard _wizard;
    private readonly ILogger<GamertagCommandHandler> _logger;

    public GamertagCommandHandler(
        GamertagWizard wizard,
        ILogger<GamertagCommandHandler> logger)
    {
        _wizard = wizard;
        _logger = logger;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Register or update your gamertags — I'll walk you through it in DMs")
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ButtonExecuted       += OnButtonExecutedAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await command.DeferAsync(ephemeral: true);
            await StartAndReportAsync(command.User, command.GuildId ?? 0,
                respond: msg => command.FollowupAsync(msg, ephemeral: true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try { await command.FollowupAsync("Something went wrong. Please try again.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId != OpenButtonId) return;

        try
        {
            await component.DeferAsync(ephemeral: true);
            await StartAndReportAsync(component.User, component.GuildId ?? 0,
                respond: msg => component.FollowupAsync(msg, ephemeral: true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling gamertags open button");
            try { await component.FollowupAsync("Something went wrong. Please try again.", ephemeral: true); }
            catch { /* already responded */ }
        }
    }

    /// <summary>
    /// Starts the wizard and reports the outcome back to the member through the
    /// supplied ephemeral responder. Shared by the slash command and the button.
    /// </summary>
    private async Task StartAndReportAsync(IUser user, ulong guildId, Func<string, Task> respond)
    {
        var started = await _wizard.StartAsync(user, guildId);
        if (started)
            await respond("📬 Check your DMs — I'll walk you through your gamertags there.");
        else
            await respond(
                "I couldn't DM you. Enable **Direct Messages** from server members (Privacy Settings) and try again.");
    }
}
