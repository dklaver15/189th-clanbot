using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Emulates a per-user exemption from Discord's voice inactivity (AFK) timeout.
///
/// Discord has no native way to exclude a specific user from being moved to the
/// guild's AFK channel. This handler listens for voice state changes and, when a
/// user listed in <see cref="BotConfig.AfkExemptUserIds"/> is moved INTO the
/// guild's AFK channel, immediately moves them back to the channel they were in.
///
/// Caveat: Discord re-arms the inactivity timer, so an idle exempt user will be
/// pulled back into AFK and returned again roughly every inactivity interval —
/// a brief flicker rather than a true exemption. Requires the bot to have the
/// "Move Members" permission.
/// </summary>
public class AfkExemptionHandler
{
    private readonly ILogger<AfkExemptionHandler> _logger;
    private readonly BotConfig _config;

    public AfkExemptionHandler(
        ILogger<AfkExemptionHandler> logger,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register event handlers on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.UserVoiceStateUpdated += OnVoiceStateUpdated;
    }

    private async Task OnVoiceStateUpdated(
        SocketUser user,
        SocketVoiceState beforeState,
        SocketVoiceState afterState)
    {
        // Only act on configured, exempt users.
        if (_config.AfkExemptUserIds is not { Count: > 0 } exemptIds
            || !exemptIds.Contains(user.Id))
            return;

        // We only care about moves that land the user IN a voice channel.
        var afterChannel = afterState.VoiceChannel;
        if (afterChannel is null) return;

        var guild = (afterChannel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        // Only react when the destination is the guild's official AFK channel.
        var afkChannel = guild.AFKChannel;
        if (afkChannel is null || afterChannel.Id != afkChannel.Id) return;

        // Move them back to wherever they were before the AFK move.
        var target = beforeState.VoiceChannel;
        if (target is null || target.Id == afkChannel.Id) return;

        try
        {
            var member = guild.GetUser(user.Id);
            if (member is null) return;

            await member.ModifyAsync(p => p.Channel = target);
            _logger.LogInformation(
                "AFK-exempt: returned {User} ({UserId}) to '{Channel}' after Discord moved them to AFK in guild {GuildId}.",
                user.Username, user.Id, target.Name, guild.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AFK-exempt: failed to return user {UserId} from AFK channel in guild {GuildId}.",
                user.Id, guild.Id);
        }
    }
}
