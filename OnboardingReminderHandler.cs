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
/// Monitors three onboarding steps for users who still have the Guest role:
///   1. Accept Rules — button click in the #rules channel
///   2. Platoon assigned — a platoon role is added via GuildMemberUpdated
///   3. Gamertag created — notified by GamertagCommandHandler after a successful save
///
/// When any step is completed by a Guest, a 24-hour reminder is persisted to the
/// database and scheduled. If the user still has the Guest role when it fires
/// (meaning they haven't created a ticket), a nudge is posted in general-chat.
/// Only one reminder per user — subsequent steps don't reset the timer.
/// </summary>
public class OnboardingReminderHandler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<OnboardingReminderHandler> _logger;
    private readonly BotConfig _config;

    private readonly ConcurrentDictionary<(ulong guildId, ulong userId), CancellationTokenSource> _pendingReminders = new();

    public OnboardingReminderHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<OnboardingReminderHandler> logger,
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
        client.ButtonExecuted += OnButtonExecuted;
        client.GuildMemberUpdated += OnGuildMemberUpdated;
        client.UserLeft += OnUserLeft;
    }

    /// <summary>
    /// Called by GamertagCommandHandler after a Guest successfully saves gamertags.
    /// </summary>
    public void NotifyGamertagCompleted(ulong guildId, ulong userId)
    {
        _ = TryScheduleReminderAsync(guildId, userId, "gamertag");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await RecoverPendingRemindersAsync(stoppingToken);
    }

    // ─── Event Handlers ──────────────────────────────────────────────

    /// <summary>Detect "Accept Rules" button click in the rules channel.</summary>
    private Task OnButtonExecuted(SocketMessageComponent component)
    {
        // Only care about clicks in the rules channel
        if (component.Channel is not SocketTextChannel textChannel)
            return Task.CompletedTask;

        if (!textChannel.Name.Equals(_config.RulesChannelName, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var user = component.User;
        if (user.IsBot) return Task.CompletedTask;

        var guildUser = user as SocketGuildUser;
        if (guildUser is null) return Task.CompletedTask;

        // Only if they still have the Guest role
        if (!HasGuestRole(guildUser)) return Task.CompletedTask;

        _logger.LogInformation(
            "Guest {Username} accepted rules in {Guild} — checking for onboarding reminder",
            guildUser.Username, textChannel.Guild.Name);

        _ = TryScheduleReminderAsync(textChannel.Guild.Id, guildUser.Id, "rules");

        return Task.CompletedTask;
    }

    /// <summary>Detect platoon role being added to a Guest.</summary>
    private Task OnGuildMemberUpdated(
        Cacheable<SocketGuildUser, ulong> beforeCacheable,
        SocketGuildUser after)
    {
        if (!beforeCacheable.HasValue) return Task.CompletedTask;
        var before = beforeCacheable.Value;

        // Only care about Guests
        if (!HasGuestRole(after)) return Task.CompletedTask;

        // Check if a platoon role was added
        var platoonRoles = _config.GetPlatoonRolesList();
        var beforeRoleNames = before.Roles.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addedPlatoon = after.Roles
            .Any(r => platoonRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase)
                   && !beforeRoleNames.Contains(r.Name));

        if (!addedPlatoon) return Task.CompletedTask;

        _logger.LogInformation(
            "Guest {Username} was assigned a platoon in {Guild} — checking for onboarding reminder",
            after.Username, after.Guild.Name);

        _ = TryScheduleReminderAsync(after.Guild.Id, after.Id, "platoon");

        return Task.CompletedTask;
    }

    /// <summary>Cancel and clean up if the user leaves the server.</summary>
    private Task OnUserLeft(SocketGuild guild, SocketUser user)
    {
        CancelReminder(guild.Id, user.Id);
        _ = RemoveReminderFromDbAsync(guild.Id, user.Id);
        return Task.CompletedTask;
    }

    // ─── Scheduling ──────────────────────────────────────────────────

    /// <summary>
    /// Schedules a 24h reminder if one doesn't already exist for this user.
    /// </summary>
    private async Task TryScheduleReminderAsync(ulong guildId, ulong userId, string triggerStep)
    {
        var key = (guildId, userId);

        // If there's already an in-memory timer running, don't create another
        if (_pendingReminders.ContainsKey(key))
        {
            _logger.LogDebug(
                "Onboarding reminder already scheduled for user {UserId} — skipping ({Step})",
                userId, triggerStep);
            return;
        }

        // Verify the user still has the Guest role
        var guild = _client.GetGuild(guildId);
        var member = guild?.GetUser(userId);
        if (member is null || !HasGuestRole(member))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var reminderAt = now.AddHours(_config.OnboardingReminderDelayHours);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Also check the DB (in case another step already persisted one)
            var existing = await db.OnboardingReminders
                .FirstOrDefaultAsync(o => o.GuildId == guildId && o.UserId == userId);

            if (existing is not null)
            {
                _logger.LogDebug(
                    "Onboarding reminder already in DB for user {UserId} — skipping ({Step})",
                    userId, triggerStep);
                return;
            }

            db.OnboardingReminders.Add(new OnboardingReminder
            {
                GuildId = guildId,
                UserId = userId,
                TriggerStep = triggerStep,
                CreatedAt = now,
                ReminderAt = reminderAt
            });
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Scheduled onboarding reminder for user {UserId} in guild {GuildId} (trigger: {Step})",
                userId, guildId, triggerStep);

            ScheduleReminder(guildId, userId, reminderAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist onboarding reminder for user {UserId} in guild {GuildId}",
                userId, guildId);
        }
    }

    private void ScheduleReminder(ulong guildId, ulong userId, DateTime reminderAtUtc)
    {
        var cts = new CancellationTokenSource();
        _pendingReminders[(guildId, userId)] = cts;

        _ = ExecuteReminderAsync(guildId, userId, reminderAtUtc, cts.Token);
    }

    private void CancelReminder(ulong guildId, ulong userId)
    {
        if (_pendingReminders.TryRemove((guildId, userId), out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ─── Reminder Execution ──────────────────────────────────────────

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
                _logger.LogDebug("User {UserId} no longer in guild — skipping onboarding reminder", userId);
                return;
            }

            // Final check: still a Guest?
            if (!HasGuestRole(member))
            {
                _logger.LogInformation(
                    "Skipping onboarding reminder for {Username} — no longer has Guest role",
                    member.Username);
                return;
            }

            var channel = guild.TextChannels.FirstOrDefault(c =>
                c.Name.Equals(_config.GuestReminderChannelName, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                _logger.LogWarning(
                    "Channel '{ChannelName}' not found in {Guild} — cannot send onboarding reminder",
                    _config.GuestReminderChannelName, guild.Name);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("📋 Almost There!")
                .WithColor(Color.Gold)
                .WithDescription(
                    $"Hey {member.Mention}, great progress on your onboarding! " +
                    "It looks like you still need to **create a ticket** to complete your entry into the clan.\n\n" +
                    "Head over to our ticket channel and click the button to open one — " +
                    "once you finish the steps inside, you'll be promoted to **Recruit** and have full access to the server!")
                .WithFooter("ClanGuard Bot • Onboarding Reminder")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await channel.SendMessageAsync(
                text: member.Mention,
                embed: embed);

            _logger.LogInformation(
                "Sent onboarding reminder for {Username} in #{Channel} ({Guild})",
                member.Username, channel.Name, guild.Name);
        }
        catch (OperationCanceledException)
        {
            // User completed onboarding or left — expected
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error sending onboarding reminder for user {UserId} in guild {GuildId}",
                userId, guildId);
        }
        finally
        {
            _pendingReminders.TryRemove((guildId, userId), out _);
            await RemoveReminderFromDbAsync(guildId, userId);
        }
    }

    // ─── Startup Recovery ────────────────────────────────────────────

    private async Task RecoverPendingRemindersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var reminders = await db.OnboardingReminders.ToListAsync(ct);

            if (reminders.Count == 0)
            {
                _logger.LogInformation("No pending onboarding reminders to recover");
                return;
            }

            _logger.LogInformation("Recovering {Count} pending onboarding reminder(s)", reminders.Count);

            var toRemove = new List<OnboardingReminder>();
            foreach (var reminder in reminders)
            {
                var guild = _client.GetGuild(reminder.GuildId);
                if (guild is null)
                {
                    toRemove.Add(reminder);
                    continue;
                }

                var member = guild.GetUser(reminder.UserId);
                if (member is null || !HasGuestRole(member))
                {
                    toRemove.Add(reminder);
                    continue;
                }

                ScheduleReminder(reminder.GuildId, reminder.UserId, reminder.ReminderAt);
            }

            if (toRemove.Count > 0)
            {
                db.OnboardingReminders.RemoveRange(toRemove);
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Cleaned up {Count} stale onboarding reminder(s)", toRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering pending onboarding reminders");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private static bool HasGuestRole(SocketGuildUser user) =>
        user.Roles.Any(r => r.Name.Equals("Guest", StringComparison.OrdinalIgnoreCase));

    private async Task RemoveReminderFromDbAsync(ulong guildId, ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = await db.OnboardingReminders
                .FirstOrDefaultAsync(o => o.GuildId == guildId && o.UserId == userId);

            if (record is not null)
            {
                db.OnboardingReminders.Remove(record);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to remove onboarding reminder for user {UserId} in guild {GuildId}",
                userId, guildId);
        }
    }
}