using ClanGuardBot.Data;
using ClanGuardBot.Models;
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

        var wasInVoice = beforeState.VoiceChannel is not null;
        var isInVoice = afterState.VoiceChannel is not null;

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
            
            if (!wasInVoice && isInVoice)
            {
                // User joined voice — start a new session
                activity.VoiceJoinedAt = DateTime.UtcNow;
                db.VoiceSessions.Add(new VoiceSession
                {
                    GuildId = guildId,
                    UserId = userId,
                    JoinedAt = DateTime.UtcNow,
                    LeftAt = null,
                    ChannelId = afterState.VoiceChannel?.Id,
                    ChannelName = afterState.VoiceChannel?.Name
                });
                _logger.LogDebug("User {Username} joined voice in guild {GuildId}", username, guildId);
            }
            else if (wasInVoice && !isInVoice)
            {
                // User left voice — close the open session
                activity.VoiceJoinedAt = null;
                var openSession = await db.VoiceSessions
                    .Where(v => v.GuildId == guildId && v.UserId == userId && v.LeftAt == null)
                    .OrderByDescending(v => v.JoinedAt)
                    .FirstOrDefaultAsync();

                if (openSession is not null)
                {
                    openSession.LeftAt = DateTime.UtcNow;
                    _logger.LogDebug("User {Username} left voice after {Duration}s in guild {GuildId}",
                        username, (DateTime.UtcNow - openSession.JoinedAt).TotalSeconds, guildId);
                }
            }
            else if (wasInVoice && isInVoice
                     && beforeState.VoiceChannel?.Id != afterState.VoiceChannel?.Id)
            {
                // User switched channels — close old session, open new one
                var openSession = await db.VoiceSessions
                    .Where(v => v.GuildId == guildId && v.UserId == userId && v.LeftAt == null)
                    .OrderByDescending(v => v.JoinedAt)
                    .FirstOrDefaultAsync();

                if (openSession is not null)
                {
                    openSession.LeftAt = DateTime.UtcNow;
                }

                activity.VoiceJoinedAt = DateTime.UtcNow;
                db.VoiceSessions.Add(new VoiceSession
                {
                    GuildId = guildId,
                    UserId = userId,
                    JoinedAt = DateTime.UtcNow,
                    LeftAt = null,
                    ChannelId = afterState.VoiceChannel?.Id,
                    ChannelName = afterState.VoiceChannel?.Name
                });
                _logger.LogDebug("User {Username} switched voice channels in guild {GuildId}", username, guildId);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking voice for user {UserId} in guild {GuildId}", userId, guildId);
        }
    }
}