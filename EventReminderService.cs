using System.Collections.Concurrent;
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
/// Posts reminders for upcoming events at the configured lead times
/// (EventReminderLeadMinutes, default 60 and 15 minutes before start). Each
/// reminder is an embed; the accompanying ping line @-mentions the organizer
/// plus every member whose RSVP is in EventReminderPingStatuses (default Going,
/// Maybe, and Waitlisted), and still posts when no one else matches.
///
/// ── Restart-safe ──
/// Fired lead values are recorded on ClanEvent.RemindersSentCsv, so a
/// `docker restart` never double-fires or loses a reminder. If the bot was down
/// across several lead times, only the most urgent (smallest) due lead fires;
/// the larger, now-stale ones are marked sent without posting.
/// </summary>
public sealed class EventReminderService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<EventReminderService> _logger;

    // Last reminder message id per event, so a newer reminder can delete the
    // previous one. In-memory: a restart mid-window just leaves the older
    // reminder (same as before this feature) — no DB column needed.
    private readonly ConcurrentDictionary<int, ulong> _lastReminderMsg = new();

    public EventReminderService(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<EventReminderService> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "EventReminderService started; polling every {Seconds}s (enabled={Enabled}, leads={Leads})",
            (int)PollInterval.TotalSeconds, _config.EventReminderEnabled, _config.EventReminderLeadMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.EventReminderEnabled)
                    await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventReminderService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var leads = ParseLeadMinutes(_config.EventReminderLeadMinutes);
        if (leads.Count == 0) return;

        var pingStatuses = ParsePingStatuses(_config.EventReminderPingStatuses);
        var maxLead = leads.Max();

        var now    = DateTime.UtcNow;
        var window = now.AddMinutes(maxLead);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Only events whose soonest reminder could be due right now. MessageId != 0
        // excludes unposted recurring-series occurrences (we only remind for the
        // one that's actually shown in #events).
        var candidates = await db.ClanEvents
            .Where(e => e.Status == ClanEventStatus.Scheduled
                     && e.MessageId != 0
                     && e.StartUtc > now
                     && e.StartUtc <= window)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var changed = false;

        foreach (var ev in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var fired = ParseCsvInts(ev.RemindersSentCsv);
            var due = leads.Where(L => !fired.Contains(L) && now >= ev.StartUtc.AddMinutes(-L)).ToList();
            if (due.Count == 0) continue;

            // Fire only the most urgent due lead; suppress any stale larger ones
            // (relevant only after downtime).
            var fire = due.Min();
            var mentions = await BuildMentionsAsync(db, ev, pingStatuses, ct);
            await PostReminderAsync(ev, mentions);

            foreach (var L in due) fired.Add(L);
            ev.RemindersSentCsv = string.Join(",", fired.OrderByDescending(x => x));
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private async Task<string> BuildMentionsAsync(
        BotDbContext db, ClanEvent ev, HashSet<EventRsvpStatus> statuses, CancellationToken ct)
    {
        // Always ping the organizer; add anyone whose RSVP matches the configured
        // statuses (deduped — the organizer may also have RSVP'd).
        var ids = new HashSet<ulong> { ev.OrganizerId };

        if (statuses.Count > 0)
        {
            var rsvpIds = await db.EventRsvps
                .Where(r => r.ClanEventId == ev.Id && statuses.Contains(r.Status))
                .Select(r => r.UserId)
                .ToListAsync(ct);
            foreach (var id in rsvpIds) ids.Add(id);
        }

        return string.Join(" ", ids.Select(id => $"<@{id}>"));
    }

    private async Task PostReminderAsync(ClanEvent ev, string mentions)
    {
        var channelId = _config.EventReminderChannelId != 0
            ? _config.EventReminderChannelId
            : _config.EventsTextChannelId;

        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            _logger.LogError(
                "Reminder channel {Channel} is not reachable; skipping reminder for '{Title}'",
                channelId, ev.Title);
            return; // lead still marked sent by caller to avoid a retry loop
        }

        // Remember the prior reminder (if any) so we can delete it once the new one
        // is up, keeping just the latest reminder in the channel.
        ulong prevMsgId = 0;
        var hadPrev = _config.EventReminderReplacePrevious
            && _lastReminderMsg.TryGetValue(ev.Id, out prevMsgId) && prevMsgId != 0;

        var hasImage = ev.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(ev.ImageFileName);

        var eb = new EmbedBuilder()
            .WithColor(new Color(0x5865F2))
            .WithTitle($"⏰ {ev.Title}")
            .WithDescription(
                $"Starts {EventTimeParser.Stamp(ev.StartUtc, 'R')}\n{EventTimeParser.Stamp(ev.StartUtc, 'F')}");
        if (hasImage)
            eb.WithImageUrl($"attachment://{ev.ImageFileName}");
        var embed = eb.Build();

        // Mentions must live in the message *content* to actually notify — pings
        // inside an embed don't fire. AllowedMentions limits this to users.
        var content = string.IsNullOrWhiteSpace(mentions) ? null : mentions;
        var allowed = new AllowedMentions { AllowedTypes = AllowedMentionTypes.Users };

        // Send with a single retry. A transient Discord blip shouldn't cost a
        // member their reminder, but we cap at one retry so a permanent failure
        // (e.g. the channel was deleted) can't spin — and the caller marks the
        // lead sent regardless, so this never loops across polls.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                IUserMessage posted;
                if (hasImage)
                {
                    using var fa = new FileAttachment(new MemoryStream(ev.ImageBytes!), ev.ImageFileName);
                    posted = await channel.SendFileAsync(fa, text: content, embed: embed, allowedMentions: allowed);
                }
                else
                {
                    posted = await channel.SendMessageAsync(text: content, embed: embed, allowedMentions: allowed);
                }

                _lastReminderMsg[ev.Id] = posted.Id;

                // New reminder is up — remove the previous one to declutter.
                if (hadPrev)
                {
                    try { await channel.DeleteMessageAsync(prevMsgId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Couldn't delete previous reminder {Msg} for '{Title}'", prevMsgId, ev.Title); }
                }
                return; // delivered
            }
            catch (Exception ex) when (attempt == 1)
            {
                _logger.LogDebug(ex, "Reminder for '{Title}' failed (attempt 1); retrying in 2s", ev.Title);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post reminder for '{Title}' after retry", ev.Title);
            }
        }
    }

    private static List<int> ParseLeadMinutes(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? n : -1)
            .Where(n => n > 0)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();

    private static HashSet<int> ParseCsvInts(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? n : -1)
            .Where(n => n > 0)
            .ToHashSet();

    private static HashSet<EventRsvpStatus> ParsePingStatuses(string csv)
    {
        var set = new HashSet<EventRsvpStatus>();
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "going":      set.Add(EventRsvpStatus.Going);      break;
                case "maybe":      set.Add(EventRsvpStatus.Maybe);      break;
                case "decline":    set.Add(EventRsvpStatus.Decline);    break;
                case "waitlisted": set.Add(EventRsvpStatus.Waitlisted); break;
            }
        }
        if (set.Count == 0) { set.Add(EventRsvpStatus.Going); set.Add(EventRsvpStatus.Maybe); set.Add(EventRsvpStatus.Waitlisted); } // sensible default
        return set;
    }
}
