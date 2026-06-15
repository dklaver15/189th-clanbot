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
/// Self-healing for in-house event posts. The DB is the source of truth — a
/// <see cref="ClanEvent"/> survives its Discord message being deleted (manually,
/// by a crashed <c>/sort</c>, by a bulk channel purge, etc.). Without repair,
/// though, that event becomes a "ghost": still Scheduled and on the calendar,
/// but invisible and unmanageable because its RSVP post is gone. This sweep
/// restores them.
///
/// Each pass (at startup and on a poll), under the shared
/// <see cref="EventChannelGate"/> so it never races the sorter, for every
/// upcoming Scheduled event in the post channel it:
///   • re-posts the RSVP message if the Discord message is missing (a clean 404),
///     re-pointing <see cref="ClanEvent.MessageId"/> and the calendar hub's
///     <c>DiscordMessageId</c>; and
///   • marks the event Cancelled if its <see cref="CalendarEvent"/> hub row is
///     gone — an unrecoverable orphan (e.g. left by an older dedupe run) that
///     can no longer sync or credit attendance — removing any stale post.
///
/// Reposts are silent (no member pings, like a sort), and a transient lookup
/// error skips the event for this pass rather than risk a duplicate repost.
/// Ended-but-not-yet-archived events are left to <see cref="EventArchiveService"/>.
/// </summary>
public sealed class ClanEventReconciliationService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly EventChannelGate _gate;
    private readonly ILogger<ClanEventReconciliationService> _logger;

    public ClanEventReconciliationService(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        EventChannelGate gate,
        ILogger<ClanEventReconciliationService> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _gate     = gate;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "ClanEventReconciliationService started; sweeping every {Minutes}m", (int)PollInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Event reconciliation sweep failed; will retry next poll"); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var channelId = _config.GetEventPostChannelId();
        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            // Usually just means the gateway isn't connected yet at startup;
            // try again next poll.
            _logger.LogDebug("Reconcile: event post channel {Channel} not reachable; skipping this pass", channelId);
            return;
        }

        // Share the sorter's per-channel lock so a heal pass and a /sort can
        // never interleave (which would race MessageId writes / double-post).
        using var hold = await _gate.AcquireAsync(channelId, ct);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var events = await db.ClanEvents
            .Where(e => e.Status == ClanEventStatus.Scheduled
                     && e.ChannelId == channelId
                     && e.EndUtc > now)
            .OrderBy(e => e.StartUtc)
            .ToListAsync(ct);

        int reposted = 0, orphansCancelled = 0;

        foreach (var ev in events)
        {
            if (ct.IsCancellationRequested) break;

            // Orphan: the calendar hub row is gone, so the event can't sync or
            // credit attendance and can't be healed — retire it and drop any
            // stale post.
            if (!await db.CalendarEvents.AnyAsync(c => c.Id == ev.CalendarEventId, ct))
            {
                ev.Status      = ClanEventStatus.Cancelled;
                ev.CancelledAt = now;
                await TryDeleteMessageAsync(channel, ev.MessageId);
                orphansCancelled++;
                _logger.LogWarning(
                    "Reconcile: ClanEvent {Id} '{Title}' has no CalendarEvent {CalId}; marked Cancelled",
                    ev.Id, ev.Title, ev.CalendarEventId);
                continue;
            }

            // Healthy if the message still exists. A transient lookup error skips
            // this event this pass — never repost on anything but a clean miss,
            // or we'd create a duplicate of a message that's actually still there.
            IMessage? msg;
            try { msg = await channel.GetMessageAsync(ev.MessageId); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reconcile: lookup failed for event {Id} msg {Msg}; skipping this pass", ev.Id, ev.MessageId);
                continue;
            }
            if (msg is not null) continue;

            // Missing → repost and re-point MessageId.
            await RepostAsync(channel, db, ev, ct);
            reposted++;
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

        if (reposted > 0 || orphansCancelled > 0)
            _logger.LogInformation(
                "Reconcile: scanned {Total} event(s); reposted {Reposted} missing, cancelled {Orphans} orphan(s)",
                events.Count, reposted, orphansCancelled);
    }

    /// <summary>
    /// Re-renders an event from current state and posts a fresh (silent) message,
    /// then re-points the DB at it. Mirrors the sorter's per-event repost — same
    /// embed/image/buttons — but is driven by a missing message rather than a
    /// reorder.
    /// </summary>
    private async Task RepostAsync(IMessageChannel channel, BotDbContext db, ClanEvent ev, CancellationToken ct)
    {
        var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync(ct);
        var (imgBytes, imgName) = await EventImage.ResolveAsync(db, ev);
        var embed = EventEmbedBuilder.BuildEmbed(ev, rsvps, imgName);

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

        var oldMsg = ev.MessageId;
        ev.MessageId = posted.Id;

        // Keep the calendar hub's message pointer in sync too, so anything that
        // still looks events up by DiscordMessageId stays coherent.
        var cal = await db.CalendarEvents.FirstOrDefaultAsync(c => c.Id == ev.CalendarEventId, ct);
        if (cal is not null) cal.DiscordMessageId = posted.Id;

        await db.SaveChangesAsync(ct); // persist the new MessageId immediately

        try
        {
            var locked = DateTime.UtcNow >= ev.StartUtc;
            await posted.ModifyAsync(m =>
                m.Components = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile: reposted event {Id} but attaching buttons failed", ev.Id);
        }

        _logger.LogInformation(
            "Reconcile: reposted missing event {Id} '{Title}' (msg {Old} → {New})", ev.Id, ev.Title, oldMsg, posted.Id);
    }

    private async Task TryDeleteMessageAsync(IMessageChannel channel, ulong messageId)
    {
        try { await channel.DeleteMessageAsync(messageId); }
        catch (Exception ex) { _logger.LogDebug(ex, "Reconcile: couldn't delete stale message {Msg}", messageId); }
    }
}
