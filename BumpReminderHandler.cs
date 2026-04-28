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
/// Reminds the server to /bump on Disboard if no bump has happened within the
/// configured window (default 2.5h — Disboard's cooldown is 2h, the extra 30
/// minutes is a soft buffer so a member who notices the timer at, say, 2h05m
/// has time to bump before the bot fires a noisy reminder).
///
/// Listens on MessageReceived for Disboard's bot ID anywhere in the guild
/// (since /bump can be invoked in any channel) and matches "Bump done" in the
/// embed description. On each observed success, the BumpState row is upserted
/// and the in-memory reminder is cancelled and re-scheduled.
///
/// On bot restart, recovers state from the DB:
///   - NextReminderAt in the future            -> schedule normally
///   - NextReminderAt missed by &lt; grace window -> fire immediately
///   - NextReminderAt missed by &gt; grace window -> reschedule to (now + delay)
///     (assume someone may have bumped during downtime; we'll observe the
///     next success or fire after another full window)
/// </summary>
public class BumpReminderHandler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<BumpReminderHandler> _logger;
    private readonly BotConfig _config;

    // One pending reminder per guild. Keyed by GuildId.
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _pendingReminders = new();

    public BumpReminderHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<BumpReminderHandler> logger,
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
        client.MessageReceived += OnMessageReceived;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.BumpReminderEnabled)
        {
            _logger.LogInformation("Bump reminder is disabled — skipping startup recovery");
            return;
        }

        // Wait for the gateway to be connected before recovering schedules.
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        await RecoverPendingRemindersAsync(stoppingToken);
    }

    // ─── Event Handlers ──────────────────────────────────────────────

    private Task OnMessageReceived(SocketMessage message)
    {
        if (!_config.BumpReminderEnabled) return Task.CompletedTask;

        // Only care about messages from Disboard's bot.
        if (message.Author.Id != _config.DisboardBotId) return Task.CompletedTask;

        if (message.Channel is not SocketGuildChannel guildChannel) return Task.CompletedTask;

        if (!IsBumpSuccess(message)) return Task.CompletedTask;

        _logger.LogInformation(
            "Detected successful Disboard bump in {Guild} #{Channel}",
            guildChannel.Guild.Name, guildChannel.Name);

        _ = HandleBumpSuccessAsync(guildChannel.Guild.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Lenient check for Disboard's success embed. Matches "Bump done" anywhere
    /// in the embed description (case-insensitive) so minor wording tweaks from
    /// Disboard don't silently break detection. Failure embeds (e.g. "Please
    /// wait another N minutes...") are intentionally ignored — the existing
    /// schedule from the previous successful bump is still correct.
    /// </summary>
    private static bool IsBumpSuccess(SocketMessage message)
    {
        foreach (var embed in message.Embeds)
        {
            var description = embed.Description ?? string.Empty;
            if (description.Contains("Bump done", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ─── Scheduling ──────────────────────────────────────────────────

    private async Task HandleBumpSuccessAsync(ulong guildId)
    {
        try
        {
            var now = DateTime.UtcNow;
            var nextReminderAt = now.AddHours(_config.BumpReminderDelayHours);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var state = await db.BumpStates.FirstOrDefaultAsync(b => b.GuildId == guildId);
            if (state is null)
            {
                state = new BumpState
                {
                    GuildId = guildId,
                    LastBumpAt = now,
                    NextReminderAt = nextReminderAt
                };
                db.BumpStates.Add(state);
            }
            else
            {
                state.LastBumpAt = now;
                state.NextReminderAt = nextReminderAt;
                // Clear the previous-cycle marker so the next firing logs cleanly.
                state.LastReminderSentAt = null;
            }

            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Recorded bump for guild {GuildId}; next reminder at {ReminderAt:u}",
                guildId, nextReminderAt);

            // Cancel any in-flight reminder and schedule fresh.
            CancelReminder(guildId);
            ScheduleReminder(guildId, nextReminderAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling bump success for guild {GuildId}", guildId);
        }
    }

    private void ScheduleReminder(ulong guildId, DateTime reminderAtUtc)
    {
        var cts = new CancellationTokenSource();
        _pendingReminders[guildId] = cts;
        _ = ExecuteReminderAsync(guildId, reminderAtUtc, cts.Token);
    }

    private void CancelReminder(ulong guildId)
    {
        if (_pendingReminders.TryRemove(guildId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ─── Reminder Execution ──────────────────────────────────────────

    private async Task ExecuteReminderAsync(ulong guildId, DateTime reminderAtUtc, CancellationToken ct)
    {
        try
        {
            var delay = reminderAtUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, ct);
            }

            // Re-read state in case a fresher bump came in while we slept and
            // the cancellation race lost.
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await db.BumpStates.FirstOrDefaultAsync(b => b.GuildId == guildId, ct);
            if (state is null) return;

            // Defensive: if NextReminderAt has been pushed out, a fresh schedule
            // is already in flight — let that one handle it.
            if (state.NextReminderAt > DateTime.UtcNow.AddMinutes(1))
            {
                _logger.LogDebug(
                    "Skipping reminder for guild {GuildId} — a fresher bump rescheduled to {NextReminderAt:u}",
                    guildId, state.NextReminderAt);
                return;
            }

            var guild = _client.GetGuild(guildId);
            if (guild is null) return;

            var channel = guild.GetTextChannel(_config.BumpReminderChannelId);
            if (channel is null)
            {
                _logger.LogWarning(
                    "Bump reminder channel {ChannelId} not found in guild {Guild}",
                    _config.BumpReminderChannelId, guild.Name);
                return;
            }

            var embed = new EmbedBuilder()
                .WithTitle("⏰ Time to Bump!")
                .WithColor(Color.Blue)
                .WithDescription(
                    "Run `/bump` to keep the 189th visible on the Disboard server list.")
                .WithFooter("ClanGuard Bot • Bump Reminder")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await channel.SendMessageAsync(embed: embed);

            state.LastReminderSentAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Sent bump reminder in {Guild} #{Channel}",
                guild.Name, channel.Name);
        }
        catch (OperationCanceledException)
        {
            // Either a fresher bump rescheduled us, or the bot is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending bump reminder for guild {GuildId}", guildId);
        }
        finally
        {
            _pendingReminders.TryRemove(guildId, out _);
        }
    }

    // ─── Startup Recovery ────────────────────────────────────────────

    private async Task RecoverPendingRemindersAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var states = await db.BumpStates.ToListAsync(ct);

            if (states.Count == 0)
            {
                _logger.LogInformation(
                    "No persisted bump state to recover — waiting for first observed bump");
                return;
            }

            var now = DateTime.UtcNow;
            var graceWindow = TimeSpan.FromMinutes(_config.BumpReminderRestartGraceMinutes);
            var rescheduled = 0;

            foreach (var state in states)
            {
                var guild = _client.GetGuild(state.GuildId);
                if (guild is null) continue;

                var timeUntilReminder = state.NextReminderAt - now;

                if (timeUntilReminder >= TimeSpan.Zero)
                {
                    // Future — schedule normally.
                    _logger.LogInformation(
                        "Recovering bump reminder for guild {GuildId}; firing at {ReminderAt:u}",
                        state.GuildId, state.NextReminderAt);
                    ScheduleReminder(state.GuildId, state.NextReminderAt);
                }
                else if (-timeUntilReminder <= graceWindow)
                {
                    // Missed within grace — fire immediately.
                    _logger.LogInformation(
                        "Bump reminder for guild {GuildId} was missed by {Minutes:F0} min (within grace) — firing now",
                        state.GuildId, -timeUntilReminder.TotalMinutes);
                    ScheduleReminder(state.GuildId, now);
                }
                else
                {
                    // Missed beyond grace — assume someone may have bumped during
                    // downtime, push the reminder out by a full window. If they
                    // did bump we'll observe the next success and reschedule;
                    // if they didn't, the reminder fires after the new window.
                    var rescheduledFor = now.AddHours(_config.BumpReminderDelayHours);
                    state.NextReminderAt = rescheduledFor;
                    rescheduled++;
                    _logger.LogInformation(
                        "Bump reminder for guild {GuildId} was missed by {Minutes:F0} min (beyond grace) — rescheduling to {ReminderAt:u}",
                        state.GuildId, -timeUntilReminder.TotalMinutes, rescheduledFor);
                    ScheduleReminder(state.GuildId, rescheduledFor);
                }
            }

            if (rescheduled > 0)
            {
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recovering pending bump reminders");
        }
    }
}