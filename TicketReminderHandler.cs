using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Watches for new channels created in the "TICKET CENTER" category.
/// When a ticket channel appears, persists a reminder record to the database
/// and schedules a delayed message. On startup, recovers any pending reminders
/// that were scheduled before the last shutdown.
/// </summary>
public class TicketReminderHandler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<TicketReminderHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>Tracks in-flight reminder tasks so we can cancel if the channel is deleted early.</summary>
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _pendingReminders = new();

    public TicketReminderHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<TicketReminderHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
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

    /// <summary>
    /// On startup, wait for Discord to connect, then recover any pending reminders
    /// that were persisted before the last shutdown.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be ready
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await RecoverPendingRemindersAsync(stoppingToken);
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

        // Fire-and-forget: persist to DB then schedule
        _ = PersistAndScheduleAsync(textChannel.Guild.Id, textChannel.Id);

        return Task.CompletedTask;
    }

    private Task OnChannelDestroyed(SocketChannel channel)
    {
        // If a ticket channel is deleted/closed before the reminder fires, cancel it
        if (_pendingReminders.TryRemove(channel.Id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        // Fire-and-forget: remove from DB
        _ = RemoveReminderFromDbAsync(channel.Id);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves a reminder record to the database, then starts the delayed task.
    /// </summary>
    private async Task PersistAndScheduleAsync(ulong guildId, ulong channelId)
    {
        try
        {
            var now = DateTime.UtcNow;
            var reminderAt = now.AddHours(_config.TicketReminderDelayHours);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Avoid duplicates (e.g. race condition)
            var existing = await db.TicketReminders
                .FirstOrDefaultAsync(t => t.ChannelId == channelId);

            if (existing is null)
            {
                db.TicketReminders.Add(new TicketReminder
                {
                    GuildId = guildId,
                    ChannelId = channelId,
                    CreatedAt = now,
                    ReminderAt = reminderAt
                });
                await db.SaveChangesAsync();
            }

            ScheduleReminder(guildId, channelId, reminderAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist ticket reminder for channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// Loads all pending reminders from the database and re-schedules them.
    /// Any that are already past due fire immediately.
    /// </summary>
    private async Task RecoverPendingRemindersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var pendingReminders = await db.TicketReminders.ToListAsync(ct);

            if (pendingReminders.Count == 0)
            {
                _logger.LogInformation("No pending ticket reminders to recover");
                return;
            }

            _logger.LogInformation("Recovering {Count} pending ticket reminder(s)", pendingReminders.Count);

            // Clean out any reminders whose channels no longer exist
            var toRemove = new List<TicketReminder>();
            foreach (var reminder in pendingReminders)
            {
                var channel = _client.GetChannel(reminder.ChannelId) as SocketTextChannel;
                if (channel is null)
                {
                    toRemove.Add(reminder);
                    continue;
                }

                ScheduleReminder(reminder.GuildId, reminder.ChannelId, reminder.ReminderAt);
            }

            if (toRemove.Count > 0)
            {
                db.TicketReminders.RemoveRange(toRemove);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Cleaned up {Count} stale ticket reminder(s) for deleted channels",
                    toRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering pending ticket reminders");
        }
    }

    /// <summary>
    /// Starts a delayed task that will fire the reminder at the specified time.
    /// If the time is already in the past, fires immediately.
    /// </summary>
    private void ScheduleReminder(ulong guildId, ulong channelId, DateTime reminderAtUtc)
    {
        var cts = new CancellationTokenSource();
        _pendingReminders[channelId] = cts;

        _ = ExecuteReminderAsync(guildId, channelId, reminderAtUtc, cts.Token);
    }

    private async Task ExecuteReminderAsync(
        ulong guildId, ulong channelId, DateTime reminderAtUtc, CancellationToken ct)
    {
        try
        {
            // Wait until it's time (if the reminder is past due, delay is zero)
            var delay = reminderAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

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
            await RemoveReminderFromDbAsync(channelId);
        }
    }

    /// <summary>Removes a reminder record from the database after it fires or is cancelled.</summary>
    private async Task RemoveReminderFromDbAsync(ulong channelId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = await db.TicketReminders
                .FirstOrDefaultAsync(t => t.ChannelId == channelId);

            if (record is not null)
            {
                db.TicketReminders.Remove(record);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove ticket reminder record for channel {ChannelId}", channelId);
        }
    }
}