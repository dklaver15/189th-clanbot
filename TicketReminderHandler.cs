using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Watches for new channels created in the "TICKET CENTER" category.
/// When a ticket channel appears, schedules a 24-hour reminder that posts
/// in the ticket if it's still open, nudging the user to complete onboarding.
/// </summary>
public class TicketReminderHandler
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<TicketReminderHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>Tracks pending reminders so we can cancel if the channel is deleted early.</summary>
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _pendingReminders = new();

    public TicketReminderHandler(
        DiscordSocketClient client,
        ILogger<TicketReminderHandler> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register event handlers on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.ChannelCreated += OnChannelCreated;
        client.ChannelDestroyed += OnChannelDestroyed;
    }

    private Task OnChannelCreated(SocketChannel channel)
    {
        // Only care about text channels inside a guild
        if (channel is not SocketTextChannel textChannel)
            return Task.CompletedTask;

        // Check if the channel is in the "TICKET CENTER" category
        var category = textChannel.Category;
        if (category is null ||
            !category.Name.Equals(_config.TicketCategoryName, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        _logger.LogInformation(
            "Ticket channel created: #{Channel} in {Guild} — scheduling 24h reminder",
            textChannel.Name, textChannel.Guild.Name);

        // Fire-and-forget the delayed reminder (tracked via CTS for cancellation)
        var cts = new CancellationTokenSource();
        _pendingReminders[textChannel.Id] = cts;

        _ = ScheduleReminderAsync(textChannel.Id, textChannel.Guild.Id, cts.Token);

        return Task.CompletedTask;
    }

    private Task OnChannelDestroyed(SocketChannel channel)
    {
        // If a ticket channel is deleted/closed before the reminder fires, cancel it
        if (_pendingReminders.TryRemove(channel.Id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _logger.LogDebug("Cancelled pending reminder for deleted channel {ChannelId}", channel.Id);
        }

        return Task.CompletedTask;
    }

    private async Task ScheduleReminderAsync(ulong channelId, ulong guildId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromHours(_config.TicketReminderDelayHours), ct);

            // Re-fetch the channel — it may have been deleted
            var channel = _client.GetChannel(channelId) as SocketTextChannel;
            if (channel is null)
            {
                _logger.LogDebug("Ticket channel {ChannelId} no longer exists — skipping reminder", channelId);
                return;
            }

            // Find the ticket creator from the channel's permission overwrites.
            // Ticket bots (Ticket Tool, etc.) add the user as an explicit overwrite.
            var guild = _client.GetGuild(guildId);
            if (guild is null) return;

            SocketGuildUser? ticketCreator = null;
            foreach (var overwrite in channel.PermissionOverwrites)
            {
                if (overwrite.TargetType != PermissionTarget.User) continue;

                var user = guild.GetUser(overwrite.TargetId);
                if (user is null || user.IsBot) continue;

                ticketCreator = user;
                break;
            }

            // Only send the reminder if the user still has the Guest role
            // (meaning they haven't completed onboarding yet)
            if (ticketCreator is not null)
            {
                var hasGuestRole = ticketCreator.Roles.Any(r =>
                    r.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase));

                if (!hasGuestRole)
                {
                    _logger.LogInformation(
                        "Skipping ticket reminder in #{Channel} — {User} no longer has the Guest role",
                        channel.Name, ticketCreator.Username);
                    return;
                }
            }

            // Build the reminder message
            var embed = new EmbedBuilder()
                .WithTitle("⏰ Ticket Reminder")
                .WithColor(Color.Gold)
                .WithDescription(
                    "Hey! Just a friendly reminder that this ticket is still open. " +
                    "Please complete the steps listed above so we can get you set up as a **Recruit** and into the action!\n\n" +
                    "If you have any questions or need help, feel free to ask here.")
                .WithFooter("ClanGuard Bot • Automatic Reminder")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var mention = ticketCreator is not null ? $"{ticketCreator.Mention} " : "";

            await channel.SendMessageAsync(
                text: mention,
                embed: embed);

            _logger.LogInformation(
                "Sent 24h ticket reminder in #{Channel} for {User}",
                channel.Name,
                ticketCreator?.Username ?? "unknown user");
        }
        catch (OperationCanceledException)
        {
            // Ticket was closed/deleted before the reminder — expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending ticket reminder for channel {ChannelId}", channelId);
        }
        finally
        {
            _pendingReminders.TryRemove(channelId, out _);
        }
    }
}