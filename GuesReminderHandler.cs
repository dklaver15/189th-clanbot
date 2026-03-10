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
/// When a new user joins the server and receives the Guest role, schedules a
/// reminder after a configurable delay (default 7 days). If the user still has
/// the Guest role when the reminder fires (meaning they haven't created a ticket
/// and completed onboarding), a nudge is posted in general-chat tagging them.
///
/// Reminders are persisted to the database so they survive bot restarts.
/// Reminders are cancelled if the user loses the Guest role or leaves the server.
/// </summary>
public class GuestReminderHandler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<GuestReminderHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>Tracks in-flight reminder tasks so we can cancel early.</summary>
    private readonly ConcurrentDictionary<(ulong guildId, ulong userId), CancellationTokenSource> _pendingReminders = new();

    public GuestReminderHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<GuestReminderHandler> logger,
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
        client.UserJoined += OnUserJoined;
        client.UserLeft += OnUserLeft;
        client.GuildMemberUpdated += OnGuildMemberUpdated;
    }

    /// <summary>
    /// On startup, wait for Discord to connect, then recover any pending reminders
    /// that were persisted before the last shutdown.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await RecoverPendingRemindersAsync(stoppingToken);
    }

    private Task OnUserJoined(SocketGuildUser user)
    {
        if (user.IsBot) return Task.CompletedTask;

        _logger.LogInformation(
            "New user joined: {Username} in {Guild} — scheduling {Days}-day guest reminder",
            user.Username, user.Guild.Name, _config.GuestReminderDelayDays);

        _ = PersistAndScheduleAsync(user.Guild.Id, user.Id);

        return Task.CompletedTask;
    }

    private Task OnUserLeft(SocketGuild guild, SocketUser user)
    {
        // User left the server — no need to remind them
        CancelReminder(guild.Id, user.Id);
        _ = RemoveReminderFromDbAsync(guild.Id, user.Id);

        return Task.CompletedTask;
    }

    private Task OnGuildMemberUpdated(
        Cacheable<SocketGuildUser, ulong> beforeCacheable,
        SocketGuildUser after)
    {
        if (!beforeCacheable.HasValue) return Task.CompletedTask;
        var before = beforeCacheable.Value;

        // Check if the Guest role was removed (user completed onboarding)
        var hadGuest = before.Roles.Any(r =>
            r.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase));
        var hasGuest = after.Roles.Any(r =>
            r.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase));

        if (hadGuest && !hasGuest)
        {
            _logger.LogInformation(
                "Guest role removed from {Username} in {Guild} — cancelling guest reminder",
                after.Username, after.Guild.Name);

            CancelReminder(after.Guild.Id, after.Id);
            _ = RemoveReminderFromDbAsync(after.Guild.Id, after.Id);
        }

        return Task.CompletedTask;
    }

    private async Task PersistAndScheduleAsync(ulong guildId, ulong userId)
    {
        try
        {
            var now = DateTime.UtcNow;
            var reminderAt = now.AddDays(_config.GuestReminderDelayDays);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var existing = await db.GuestReminders
                .FirstOrDefaultAsync(g => g.GuildId == guildId && g.UserId == userId);

            if (existing is null)
            {
                db.GuestReminders.Add(new GuestReminder
                {
                    GuildId = guildId,
                    UserId = userId,
                    JoinedAt = now,
                    ReminderAt = reminderAt
                });
                await db.SaveChangesAsync();
            }

            ScheduleReminder(guildId, userId, reminderAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist guest reminder for user {UserId} in guild {GuildId}",
                userId, guildId);
        }
    }

    private async Task RecoverPendingRemindersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var pendingReminders = await db.GuestReminders.ToListAsync(ct);

            if (pendingReminders.Count == 0)
            {
                _logger.LogInformation("No pending guest reminders to recover");
                return;
            }

            _logger.LogInformation("Recovering {Count} pending guest reminder(s)", pendingReminders.Count);

            var toRemove = new List<GuestReminder>();
            foreach (var reminder in pendingReminders)
            {
                var guild = _client.GetGuild(reminder.GuildId);
                if (guild is null)
                {
                    toRemove.Add(reminder);
                    continue;
                }

                var member = guild.GetUser(reminder.UserId);
                if (member is null)
                {
                    // User left the server while bot was down
                    toRemove.Add(reminder);
                    continue;
                }

                // If they already lost the Guest role, no need to remind
                var hasGuest = member.Roles.Any(r =>
                    r.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase));
                if (!hasGuest)
                {
                    toRemove.Add(reminder);
                    continue;
                }

                ScheduleReminder(reminder.GuildId, reminder.UserId, reminder.ReminderAt);
            }

            if (toRemove.Count > 0)
            {
                db.GuestReminders.RemoveRange(toRemove);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Cleaned up {Count} stale guest reminder(s)", toRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering pending guest reminders");
        }
    }

    private void ScheduleReminder(ulong guildId, ulong userId, DateTime reminderAtUtc)
    {
        var cts = new CancellationTokenSource();
        var key = (guildId, userId);
        _pendingReminders[key] = cts;

        _ = ExecuteReminderAsync(guildId, userId, reminderAtUtc, cts.Token);
    }

    private void CancelReminder(ulong guildId, ulong userId)
    {
        var key = (guildId, userId);
        if (_pendingReminders.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task ExecuteReminderAsync(
        ulong guildId, ulong userId, DateTime reminderAtUtc, CancellationToken ct)
    {
        try
        {
            var delay = reminderAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            var guild = _client.GetGuild(guildId);
            if (guild is null) return;

            var member = guild.GetUser(userId);
            if (member is null)
            {
                _logger.LogDebug("User {UserId} no longer in guild — skipping guest reminder", userId);
                return;
            }

            // Final check: do they still have the Guest role?
            var hasGuest = member.Roles.Any(r =>
                r.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase));

            if (!hasGuest)
            {
                _logger.LogInformation(
                    "Skipping guest reminder for {Username} — no longer has Guest role",
                    member.Username);
                return;
            }

            // Find the general-chat channel
            var channel = guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(_config.GuestReminderChannelName, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                _logger.LogWarning(
                    "Guest reminder channel '{ChannelName}' not found in {Guild} — cannot send reminder",
                    _config.GuestReminderChannelName, guild.Name);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("👋 Don't Forget to Join Up!")
                .WithColor(Color.Blue)
                .WithDescription(
                    $"Hey {member.Mention}! We noticed you joined the server a week ago but haven't created a ticket yet to become a **Recruit**.\n\n" +
                    "Getting set up is easy — just head over to our ticket channel and click the button to open one. " +
                    "Once you complete the steps, you'll have full access to the server!\n\n" +
                    "If you have any questions or need help, feel free to ask here.")
                .WithFooter("ClanGuard Bot • Guest Reminder")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await channel.SendMessageAsync(
                text: member.Mention,
                embed: embed);

            _logger.LogInformation(
                "Sent guest reminder for {Username} in #{Channel} ({Guild})",
                member.Username, channel.Name, guild.Name);
        }
        catch (OperationCanceledException)
        {
            // User completed onboarding or left — expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending guest reminder for user {UserId} in guild {GuildId}",
                userId, guildId);
        }
        finally
        {
            _pendingReminders.TryRemove((guildId, userId), out _);
            await RemoveReminderFromDbAsync(guildId, userId);
        }
    }

    private async Task RemoveReminderFromDbAsync(ulong guildId, ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = await db.GuestReminders
                .FirstOrDefaultAsync(g => g.GuildId == guildId && g.UserId == userId);

            if (record is not null)
            {
                db.GuestReminders.Remove(record);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove guest reminder for user {UserId} in guild {GuildId}",
                userId, guildId);
        }
    }
}