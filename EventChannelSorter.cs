using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Sorts the event channel chronologically — the in-house equivalent of
/// Apollo's /sort. Discord can't reorder existing messages, so this deletes
/// every scheduled event's post and re-posts them in StartUtc order (soonest
/// first / at the top).
///
/// ── Safe to do, unlike Apollo's sort ──
/// RSVPs are keyed by ClanEventId (not the message), so they survive the
/// repost. Nothing watches our posts, so the CalendarEvent rows and Google
/// Calendar entries are completely untouched — no GCal churn, no rebind dance.
/// Only ClanEvent.MessageId changes, which is updated here, keeping reminders
/// and auto-archive pointed at the live message.
/// </summary>
public sealed class EventChannelSorter
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly EventChannelGate _gate;
    private readonly ILogger<EventChannelSorter> _logger;

    public EventChannelSorter(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        EventChannelGate gate,
        ILogger<EventChannelSorter> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _gate     = gate;
        _logger   = logger;
    }

    /// <summary>
    /// Re-posts every scheduled event in the current post channel in
    /// chronological order. Returns the number of events re-posted.
    /// </summary>
    public async Task<int> SortAsync(ulong guildId)
    {
        var channelId = _config.GetEventPostChannelId();
        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            _logger.LogError("Event post channel {Channel} is not reachable; cannot sort", channelId);
            return 0;
        }

        // Shared per-channel gate: a sort and the self-healing repost sweep
        // (ClanEventReconciliationService) must never run at the same time.
        using var hold = await _gate.AcquireAsync(channelId);
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var events = await db.ClanEvents
                .Where(e => e.GuildId == guildId
                         && e.Status == ClanEventStatus.Scheduled
                         && e.ChannelId == channelId)
                .OrderBy(e => e.StartUtc)
                .ToListAsync();

            if (events.Count <= 1) return events.Count; // nothing to reorder

            var ids = events.Select(e => e.Id).ToList();
            var rsvpsByEvent = (await db.EventRsvps.Where(r => ids.Contains(r.ClanEventId)).ToListAsync())
                .GroupBy(r => r.ClanEventId)
                .ToDictionary(g => g.Key, g => (IReadOnlyCollection<EventRsvp>)g.ToList());

            // 1) Delete the existing posts so the re-posted ones are the only
            //    event messages and land in order.
            foreach (var ev in events)
            {
                try { await channel.DeleteMessageAsync(ev.MessageId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Sort: couldn't delete old message {Msg} for event {Id}", ev.MessageId, ev.Id); }
            }

            // 2) Re-post in chronological order, persisting each new MessageId
            //    immediately so the DB stays consistent with Discord even if the
            //    pass is interrupted partway.
            foreach (var ev in events)
            {
                var rsvps = rsvpsByEvent.TryGetValue(ev.Id, out var list) ? list : Array.Empty<EventRsvp>();
                var (imgBytes, imgName) = await EventImage.ResolveAsync(db, ev);
                var embed = EventEmbedBuilder.BuildEmbed(ev, rsvps, imgName);

                // Sort re-posts every event at once, so fire them as silent
                // messages — they appear normally but raise no push/desktop/unread
                // ping, even for members with "All Messages" on this channel.
                // Without this a single /sort would notify the whole channel N
                // times. (Event embeds carry mentions only in fields, which never
                // ping anyway; the suppressed vector is the per-message alert.)
                IUserMessage posted;
                if (imgBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(imgName))
                {
                    using var fa = new FileAttachment(new MemoryStream(imgBytes), imgName);
                    posted = await channel.SendFileAsync(fa, embed: embed, flags: MessageFlags.SuppressNotification);
                }
                else
                {
                    posted = await channel.SendMessageAsync(embed: embed, flags: MessageFlags.SuppressNotification);
                }

                ev.MessageId = posted.Id;
                await db.SaveChangesAsync();

                try
                {
                    var locked = DateTime.UtcNow >= ev.StartUtc;
                    await posted.ModifyAsync(m => m.Components = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sort: re-posted event {Id} but failed to attach buttons", ev.Id);
                }
            }

            _logger.LogInformation("Sorted {Count} event post(s) in channel {Channel}", events.Count, channelId);
            return events.Count;
        }
    }
}
