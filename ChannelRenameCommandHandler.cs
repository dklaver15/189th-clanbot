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
        //
        // Discord strips newline characters from channel names, so a real two-
        // ROW name can't be made with a line break. The effect is achieved by
        // wrapping: line1 stays plain readable text, and line2 is converted to
        // WIDE native emoji (regional-indicator letters + keycap digits) that
        // overflow the narrow sidebar and wrap onto a second visual line — the
        // same technique the official Battlefield server uses for "Lobby /
        // 5K HOURS". Custom server emoji are NOT allowed in channel names, so
        // only native emoji work, which is exactly what this produces.
        string newName;
        if (!string.IsNullOrWhiteSpace(line2))
        {
            newName = $"{line1} {EmojifyForWrap(line2)}";
        }
        else
        {
            newName = line1;
        }

        if (newName.Length > MaxNameLength)
        {
            await command.FollowupAsync(
                $"❌ That name is {newName.Length} characters — Discord's limit is {MaxNameLength}. " +
                "The emoji on the second line each count for several characters, so try a shorter line 2.",
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
            oldName, newName, channel.Id, caller.Username);

        await command.FollowupAsync(
            $"✅ **Channel Renamed** to:\n{newName}\n\n" +
            "_The emoji should wrap to a second line in the sidebar. Rendering varies by " +
            "device — check on mobile and desktop. If it didn't wrap, line 2 may be too short " +
            "to overflow; add a little more._",
            ephemeral: true);
    }

    /// <summary>
    /// Converts plain text into a string of WIDE native Unicode emoji so it
    /// overflows the channel sidebar and wraps to a second line:
    ///   • A–Z  → regional-indicator letters (the blue block letters)
    ///   • 0–9  → keycap digit emoji
    ///   • anything else passes through unchanged
    ///
    /// Each emitted glyph is followed by a space. The spaces are load-bearing:
    /// two adjacent regional indicators that form a valid country code render
    /// as that country's FLAG instead of two letters (e.g. U+R → 🇺🇷 Uruguay),
    /// and a separating space defeats that pairing. The gaps also match the
    /// spaced-out look of the Battlefield server's "5K HOURS".
    /// </summary>
    private static string EmojifyForWrap(string text)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                // Regional indicator A (U+1F1E6) offset by the letter index.
                var codePoint = 0x1F1E6 + (char.ToUpperInvariant(ch) - 'A');
                sb.Append(char.ConvertFromUtf32(codePoint));
                sb.Append(' ');
            }
            else if (ch is >= '0' and <= '9')
            {
                // Keycap sequence: digit + VS16 + combining enclosing keycap.
                sb.Append(ch);
                sb.Append('\uFE0F');
                sb.Append('\u20E3');
                sb.Append(' ');
            }
            else if (ch == ' ')
            {
                // Word break → an extra gap between the emoji "words".
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }

    private bool HasPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageChannels || user.GuildPermissions.Administrator)
            return true;

        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}
