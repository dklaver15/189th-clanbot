using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /lookup slash command. Takes a Discord user picker and returns
/// that user's gamertags from the roster sheet (ephemeral, open to any member).
/// </summary>
public class LookupCommandHandler
{
    private readonly GoogleSheetsService _sheetsService;
    private readonly ILogger<LookupCommandHandler> _logger;

    public LookupCommandHandler(
        GoogleSheetsService sheetsService,
        ILogger<LookupCommandHandler> logger)
    {
        _sheetsService = sheetsService;
        _logger = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != "lookup") return;

        await command.DeferAsync(ephemeral: true);

        try
        {
            var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "user");
            if (userOption?.Value is not SocketUser targetUser)
            {
                await command.FollowupAsync("⚠️ You must select a user to look up.", ephemeral: true);
                return;
            }

            var displayName = targetUser.GlobalName ?? targetUser.Username;
            var result = await _sheetsService.LookupGamertagsAsync(targetUser.Id, displayName);

            if (result is null)
            {
                await command.FollowupAsync(
                    $"❓ No gamertags found for **{displayName}**. They may not have run `/gamertags` yet.",
                    ephemeral: true);
                return;
            }

            var embed = new EmbedBuilder()
                .WithAuthor(displayName, targetUser.GetDisplayAvatarUrl())
                .WithTitle("🎮 Gamertags")
                .WithColor(Color.Blue)
                .AddField("EA", string.IsNullOrWhiteSpace(result.EA) ? "—" : result.EA, true)
                .AddField("Steam", string.IsNullOrWhiteSpace(result.Steam) ? "—" : result.Steam, true)
                .AddField("PSN", string.IsNullOrWhiteSpace(result.PSN) ? "—" : result.PSN, true)
                .AddField("Xbox", string.IsNullOrWhiteSpace(result.Xbox) ? "—" : result.Xbox, true)
                .AddField("Embark", string.IsNullOrWhiteSpace(result.Embark) ? "—" : result.Embark, true)
                .AddField("Bungie", string.IsNullOrWhiteSpace(result.Bungie) ? "—" : result.Bungie, true)
                .WithFooter("Looked up from the roster sheet.")
                .Build();

            await command.FollowupAsync(embed: embed, ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to look up gamertags for command issued by {User}", command.User.Username);
            await command.FollowupAsync(
                "❌ Failed to look up gamertags. Please try again or contact an admin.",
                ephemeral: true);
        }
    }
}