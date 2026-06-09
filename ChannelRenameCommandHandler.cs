using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /channel-rename slash command — renames a voice or stage
/// channel and (optionally) embeds a hard newline so the name renders on two
/// lines in the channel sidebar.
///
/// Discord's web/desktop UI strips newlines from the channel-name field, but
/// the API accepts a literal LF, so the bot can set what the client cannot.
/// This works on Voice and Stage channels (which preserve case, spaces, and
/// newlines); text-channel names are sanitized by Discord (lowercased,
/// spaces → hyphens, newlines dropped), so the option is restricted to
/// voice/stage in the command definition.
/// </summary>
public class ChannelRenameCommandHandler
{
    public const string CommandName = "channel-rename";

    // Discord's channel-name limit.
    private const int MaxNameLength = 100;

    private readonly ILogger<ChannelRenameCommandHandler> _logger;
    private readonly BotConfig _config;

    public ChannelRenameCommandHandler(
        ILogger<ChannelRenameCommandHandler> logger,
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
        if (command.Data.Name is not CommandName)
            return;

        try
        {
            await HandleRename(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleRename(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

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

        var channelOption = command.Data.Options.FirstOrDefault(o => o.Name == "channel");
        var line1Option   = command.Data.Options.FirstOrDefault(o => o.Name == "line1");
        var line2Option   = command.Data.Options.FirstOrDefault(o => o.Name == "line2");

        if (channelOption?.Value is not IGuildChannel channel)
        {
            await command.FollowupAsync("❌ Could not resolve that channel.", ephemeral: true);
            return;
        }

        var line1 = line1Option?.Value?.ToString() ?? string.Empty;
        var line2 = line2Option?.Value?.ToString();

        if (string.IsNullOrWhiteSpace(line1))
        {
            await command.FollowupAsync("❌ The first line can't be empty.", ephemeral: true);
            return;
        }

        // Build the final name.
        //  - If line2 is supplied, join the two with a real LF.
        //  - Otherwise, let a power user embed the line break themselves by
        //    typing the literal two-character sequence \n in line1, which we
        //    convert to a real newline here.
        string newName;
        if (!string.IsNullOrWhiteSpace(line2))
        {
            newName = $"{line1}\n{line2}";
        }
        else
        {
            newName = line1.Replace("\\n", "\n");
        }

        if (newName.Length > MaxNameLength)
        {
            await command.FollowupAsync(
                $"❌ That name is {newName.Length} characters — Discord's limit is {MaxNameLength}. " +
                "Shorten it and try again.",
                ephemeral: true);
            return;
        }

        var oldName = channel.Name;

        try
        {
            await channel.ModifyAsync(c => c.Name = newName);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Could not rename channel {Channel} ({Id}): {Error}",
                oldName, channel.Id, ex.Message);

            await command.FollowupAsync(
                "❌ Discord refused the rename (insufficient permissions — the bot needs " +
                "**Manage Channels**, and its role must be able to manage that channel).",
                ephemeral: true);
            return;
        }
        catch (Discord.Net.HttpException ex)
        {
            // e.g. rate-limited by Discord's stricter channel-rename bucket
            // (2 renames / 10 minutes per channel), or a validation rejection.
            _logger.LogWarning("Discord rejected rename of {Channel} ({Id}): {Error}",
                oldName, channel.Id, ex.Message);

            await command.FollowupAsync(
                $"❌ Discord rejected the rename: {ex.Reason ?? ex.Message}\n" +
                "Note: channel renames are rate-limited to roughly **2 per 10 minutes** per channel.",
                ephemeral: true);
            return;
        }

        _logger.LogInformation("Channel renamed: {Old} → {New} (#{Id}) by {Caller}",
            oldName, newName.Replace("\n", "\\n"), channel.Id, caller.Username);

        // Show the multi-line result in a code block so the newline is visible.
        await command.FollowupAsync(
            $"✅ **Channel Renamed**\n```\n{newName}\n```",
            ephemeral: true);
    }

    private bool HasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageChannels || user.GuildPermissions.Administrator)
            return true;

        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}
