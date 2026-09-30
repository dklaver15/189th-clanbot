using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles Discord gateway events to track user activity:
///   - MessageReceived: increments message count
///   - UserVoiceStateUpdated: tracks voice channel join/leave times
/// </summary>
public class ActivityTrackingHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ActivityTrackingHandler> _logger;
    private readonly BotConfig _config;

    public ActivityTrackingHandler(
        IServiceProvider services,
        ILogger<ActivityTrackingHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register event handlers on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
        client.UserVoiceStateUpdated += OnVoiceStateUpdated;
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot || message is not SocketUserMessage userMessage)
            return;

        if (userMessage.Channel is not SocketGuildChannel guildChannel)
            return;

        var guildId = guildChannel.Guild.Id;
        var userId = message.Author.Id;
        var username = message.Author.ToString() ?? message.Author.Username;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Ensure user activity record exists
            var activity = await db.UserActivities
                .FirstOrDefaultAsync(a => a.GuildId == guildId && a.UserId == userId);

            if (activity is null)
            {
                activity = new UserActivity
                {
                    GuildId = guildId,
                    UserId = userId,
                    Username = username
                };
                db.UserActivities.Add(activity);
            }
            else
            {
                activity.Username = username;
            }

            // Record the message event
            db.MessageEvents.Add(new MessageEvent
            {
                GuildId = guildId,
                UserId = userId,
                Timestamp = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking message for user {UserId} in guild {GuildId}", userId, guildId);
        }
    }

    private static SocketVoiceChannel? CountedChannel(SocketVoiceChannel? channel, ulong? afkChannelId) =>
        channel is not null && channel.Id != afkChannelId ? channel : null;

    private async Task OnVoiceStateUpdated(
        SocketUser user,
        SocketVoiceState beforeState,
        SocketVoiceState afterState)
    {
        if (user.IsBot) return;

        var guild = (beforeState.VoiceChannel as SocketGuildChannel)?.Guild
                 ?? (afterState.VoiceChannel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        var guildId = guild.Id;
        var userId = user.Id;
        var username = user.ToString() ?? user.Username;

        // Time in the guild's AFK channel isn't activity: moving there counts as
        // leaving voice, and moving back out as joining.
        var afkChannelId = guild.AFKChannel?.Id;
        var beforeChannel = CountedChannel(beforeState.VoiceChannel, afkChannelId);
        var afterChannel  = CountedChannel(afterState.VoiceChannel, afkChannelId);

        var wasInVoice = beforeChannel is not null;
        var isInVoice  = afterChannel is not null;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var activity = await db.UserActivities
                .FirstOrDefaultAsync(a => a.GuildId == guildId && a.UserId == userId);

            if (activity is null)
            {
                activity = new UserActivity
                {
                    GuildId = guildId,
                    UserId = userId,
                    Username = username
                };
                db.UserActivities.Add(activity);
            }
            else
            {
                activity.Username = username;
            }

            var switched = wasInVoice && isInVoice && beforeChannel!.Id != afterChannel!.Id;
            var now = DateTime.UtcNow;

            if (wasInVoice != isInVoice || switched)
            {
                // Close every open session, not just the newest: one left open by
                // a missed leave would otherwise keep counting alongside the new one.
                var openSessions = await db.VoiceSessions
                    .Where(v => v.GuildId == guildId && v.UserId == userId && v.LeftAt == null)
                    .ToListAsync();
                foreach (var open in openSessions)
                    open.LeftAt = VoiceActivityHelper.ClampLeftAt(open.JoinedAt, _config.MaxSingleSessionHours, now);
            }

            if (isInVoice && (!wasInVoice || switched))
            {
                activity.VoiceJoinedAt = now;
                db.VoiceSessions.Add(new VoiceSession
                {
                    GuildId = guildId,
                    UserId = userId,
                    JoinedAt = now,
                    LeftAt = null,
                    ChannelId = afterChannel!.Id,
                    ChannelName = afterChannel.Name,
                    CategoryId = afterChannel.CategoryId
                });
                _logger.LogDebug("User {Username} {Action} voice in guild {GuildId}",
                    username, switched ? "switched" : "joined", guildId);
            }
            else if (wasInVoice && !isInVoice)
            {
                activity.VoiceJoinedAt = null;
                _logger.LogDebug("User {Username} left voice in guild {GuildId}", username, guildId);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking voice for user {UserId} in guild {GuildId}", userId, guildId);
        }
    }
}