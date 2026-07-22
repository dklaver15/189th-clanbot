using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Fires scheduled reminders authored via <c>/reminder</c>. Polls every minute
/// for <see cref="ClanReminder"/> rows that are Scheduled and due
/// (<see cref="ClanReminder.NextFireUtc"/> ≤ now), posts each as an embed to its
/// target channel — with any pings in the message content above it — then either
/// completes it (one-off) or advances it to its next occurrence (recurring).
///
/// ── Restart-safe ──
/// The next fire instant lives in the DB, so a <c>docker restart</c> never loses
/// or double-fires a reminder. If the bot was down across a fire time, the
/// reminder posts on the next tick after it comes back (a little late rather than
/// never). For a recurring reminder that missed several occurrences while down,
/// the recurrence advance skips the stale ones to the next FUTURE occurrence.
///
/// ── DST-safe recurrence ──
/// Each occurrence is anchored in the creator's LOCAL wall-clock
/// (<see cref="ClanReminder.FirstFireLocal"/> advanced by the frequency) and
/// converted to UTC fresh, so a weekly 8 AM reminder stays 8 AM local across the
/// spring/fall transition — identical to the event recurrence engine.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.RemindersEnabled"/> is true, so the whole
/// feature is a pure config switch.
/// </summary>
public sealed class ReminderSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);
    private static readonly Color EmbedColor = new(0x5865F2);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<ReminderSchedulerService> _logger;

    public ReminderSchedulerService(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<ReminderSchedulerService> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the gateway so channel resolution / posting is usable.
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "ReminderSchedulerService started; polling every {Seconds}s (enabled={Enabled})",
            (int)PollInterval.TotalSeconds, _config.RemindersEnabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.RemindersEnabled)
                    await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReminderSchedulerService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var due = await db.ClanReminders
            .Where(r => r.Status == ClanReminderStatus.Scheduled && r.NextFireUtc <= now)
            .OrderBy(r => r.NextFireUtc)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        foreach (var reminder in due)
        {
            if (ct.IsCancellationRequested) break;

            // Best-effort post. Whether it succeeds or not, we advance the
            // schedule so a permanently-unreachable channel can't spin the loop.
            await TryPostAsync(reminder);
            reminder.LastFiredAt = DateTime.UtcNow;

            AdvanceOrComplete(reminder, DateTime.UtcNow);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Posts one reminder to its channel. Swallows all failures (returns void).</summary>
    private async Task TryPostAsync(ClanReminder r)
    {
        var channel = _client.GetChannel(r.ChannelId) as IMessageChannel
                      ?? await SafeRestChannelAsync(r.ChannelId);
        if (channel is null)
        {
            _logger.LogWarning(
                "ReminderScheduler: channel {Channel} for reminder {Id} '{Title}' is not reachable; skipping this fire",
                r.ChannelId, r.Id, r.Title);
            return;
        }

        var roleIds = SplitIds(r.PingRoleIdsCsv);
        var userIds = SplitIds(r.PingUserIdsCsv);
        var content = ReminderTargets.BuildMentionContent(roleIds, userIds, r.PingEveryone, r.PingHere);
        var allowed = ReminderTargets.BuildAllowedMentions(roleIds, userIds, r.PingEveryone, r.PingHere);

        var eb = new EmbedBuilder()
            .WithColor(EmbedColor)
            .WithTitle(Truncate(r.Title, 256))
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(r.Description))
            eb.WithDescription(Truncate(r.Description, 4096));

        if (!string.IsNullOrWhiteSpace(r.Url) && Uri.TryCreate(r.Url, UriKind.Absolute, out _))
        {
            eb.WithUrl(r.Url);
            eb.AddField("More info", r.Url, inline: false);
        }

        var hasImage = r.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(r.ImageFileName);
        if (hasImage)
            eb.WithImageUrl($"attachment://{r.ImageFileName}");

        var embed = eb.Build();
        var text  = string.IsNullOrWhiteSpace(content) ? null : content;

        // Single retry — a transient Discord blip shouldn't cost the announcement,
        // but cap at one so a permanent failure can't spin.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (hasImage)
                {
                    using var fa = new FileAttachment(new MemoryStream(r.ImageBytes!), r.ImageFileName);
                    await channel.SendFileAsync(fa, text: text, embed: embed, allowedMentions: allowed);
                }
                else
                {
                    await channel.SendMessageAsync(text: text, embed: embed, allowedMentions: allowed);
                }

                _logger.LogInformation(
                    "ReminderScheduler posted reminder {Id} '{Title}' to channel {Channel} (pings={Pings})",
                    r.Id, r.Title, r.ChannelId, roleIds.Count + userIds.Count + (r.PingEveryone ? 1 : 0) + (r.PingHere ? 1 : 0));
                return;
            }
            catch (Exception ex) when (attempt == 1)
            {
                _logger.LogDebug(ex, "Reminder {Id} '{Title}' post failed (attempt 1); retrying in 2s", r.Id, r.Title);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post reminder {Id} '{Title}' after retry", r.Id, r.Title);
            }
        }
    }

    private async Task<IMessageChannel?> SafeRestChannelAsync(ulong channelId)
    {
        try { return await _client.Rest.GetChannelAsync(channelId) as IMessageChannel; }
        catch { return null; }
    }

    /// <summary>
    /// One-off → Completed. Recurring → advance <see cref="ClanReminder.NextFireUtc"/>
    /// to the next FUTURE occurrence (skipping any missed while the bot was down),
    /// or Completed once the Until/MaxOccurrences bound is reached.
    /// </summary>
    private void AdvanceOrComplete(ClanReminder r, DateTime now)
    {
        if (r.Frequency is not ClanEventFrequency freq)
        {
            r.Status = ClanReminderStatus.Completed;
            return;
        }

        var tz = ResolveZone(r.TimeZoneId);
        var i  = r.NextOccurrenceIndex + 1;
        var guard = i + 100_000; // absurd-config backstop

        while (i < guard)
        {
            if (r.MaxOccurrences is int max && i >= max) break;

            var local = Advance(r.FirstFireLocal, freq, i);
            var utc   = LocalToUtc(local, tz);

            if (r.UntilUtc is DateTime until && utc > until) break;

            if (utc > now)
            {
                r.NextOccurrenceIndex = i;
                r.NextFireUtc = utc;
                return; // still scheduled
            }
            i++; // occurrence already elapsed (missed while down) — skip forward
        }

        r.Status = ClanReminderStatus.Completed;
    }

    // ─── Recurrence math (mirrors ClanEventRecurrence — DST-safe) ──────────

    private static DateTime Advance(DateTime localAnchor, ClanEventFrequency freq, int n) => freq switch
    {
        ClanEventFrequency.Daily    => localAnchor.AddDays(n),
        ClanEventFrequency.Weekly   => localAnchor.AddDays(7 * n),
        ClanEventFrequency.Biweekly => localAnchor.AddDays(14 * n),
        ClanEventFrequency.Monthly  => localAnchor.AddMonths(n),
        _                           => localAnchor.AddDays(n),
    };

    private static DateTime LocalToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
            local = local.AddHours(1); // spring-forward gap → shift into a valid wall-clock time
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    private TimeZoneInfo ResolveZone(string? ianaId)
    {
        if (!string.IsNullOrWhiteSpace(ianaId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var tz) && tz is not null)
            return tz;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(_config.EventDefaultTimeZone, out var def) && def is not null)
            return def;
        return TimeZoneInfo.Utc;
    }

    private static List<ulong> SplitIds(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => ulong.TryParse(p, out var n) ? n : 0UL)
            .Where(n => n != 0)
            .ToList();

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");
}
