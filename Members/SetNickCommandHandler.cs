using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /setnick slash command — allows officers to change a member's nickname.
/// </summary>
public class SetNickCommandHandler
{
    private readonly ILogger<SetNickCommandHandler> _logger;
    private readonly BotConfig _config;

    public SetNickCommandHandler(
        ILogger<SetNickCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not "setnick")
            return;

        try
        {
            await HandleSetNick(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /setnick");
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleSetNick(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasPermission(caller))
        {
            await command.FollowupAsync("❌ You don't have permission to use this command.", ephemeral: true);
            return;
        }

        var memberOption = command.Data.Options.FirstOrDefault(o => o.Name == "member");
        var nicknameOption = command.Data.Options.FirstOrDefault(o => o.Name == "nickname");

        var member = memberOption?.Value as SocketGuildUser;
        var nickname = nicknameOption?.Value?.ToString() ?? string.Empty;

        if (member is null)
        {
            await command.FollowupAsync("❌ Could not find that member.", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(nickname))
        {
            await command.FollowupAsync("❌ Please provide a nickname.", ephemeral: true);
            return;
        }

        try
        {
            await member.ModifyAsync(p => p.Nickname = nickname);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Could not update nickname for {User}: {Error}",
                member.Username, ex.Message);

            await command.FollowupAsync(
                $"❌ Could not update nickname for {member.Mention} (insufficient permissions).",
                ephemeral: true);
            return;
        }

        _logger.LogInformation("Nickname changed: {Member} → {NewNick} by {Caller}",
            member.Username, nickname, caller.Username);

        await command.FollowupAsync(
            $"✅ **Nickname Updated**: {member.Mention} is now `{nickname}`.");
    }

    private bool HasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageNicknames || user.GuildPermissions.Administrator)
            return true;

        var officerRoles = _config.GetOfficerRolesList();
        return user.Roles.Any(r => officerRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}