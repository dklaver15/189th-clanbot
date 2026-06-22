using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the cosmetic RSVP buttons on event posts (custom id
/// <c>evt:rsvp:&lt;status&gt;:&lt;clanEventId&gt;</c>). Upserts the member's
/// <see cref="EventRsvp"/> and re-renders the post via
/// <see cref="EventEmbedBuilder"/>. RSVP never affects attendance credit, which
/// is voice-based — these clicks only change who shows in the rosters and who
/// gets pinged by the reminder.
///
/// ── Concurrency ──
/// Two members clicking the same post race the read-modify-edit of the embed
/// (the same hazard Apollo's MessageReceived/MessageUpdated had). A per-event
/// semaphore serializes the critical section so neither click overwrites the
/// other's roster render.
/// </summary>
public sealed class EventRsvpInteractionHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EventRsvpInteractionHandler> _logger;
    private readonly BotConfig _config;
    private readonly DiscordSocketClient _client;

    // Striped locks: a fixed pool of gates indexed by event id. Serializes the
    // read-modify-render of a given event's embed without growing per-event
    // (the previous per-id dictionary was never pruned, a slow leak). Two
    // distinct events may occasionally share a stripe — harmless, since the
    // critical section is brief.
    private const int LockStripeCount = 64;
    private readonly SemaphoreSlim[] _locks =
        Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private SemaphoreSlim GateFor(int clanEventId) => _locks[(uint)clanEventId % LockStripeCount];

    public EventRsvpInteractionHandler(
        IServiceProvider services,
        ILogger<EventRsvpInteractionHandler> logger,
        IOptions<BotConfig> config,
        DiscordSocketClient client)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
        _client   = client;
    }

    public void Register(DiscordSocketClient client)
    {
        client.ButtonExecuted += OnButtonExecutedAsync;
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(EventEmbedBuilder.RsvpPrefix, StringComparison.Ordinal))
            return;

        // evt:rsvp:<status>:<clanEventId>
        var parts = component.Data.CustomId.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[3], out var clanEventId))
        {
            await component.DeferAsync();
            return;
        }

        var status = parts[2] switch
        {
            "going"   => EventRsvpStatus.Going,
            "maybe"   => EventRsvpStatus.Maybe,
            "decline" => EventRsvpStatus.Decline,
            _         => (EventRsvpStatus?)null,
        };
        if (status is null)
        {
            await component.DeferAsync();
            return;
        }

        var gate = GateFor(clanEventId);
        await gate.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
            if (ev is null || ev.Status == ClanEventStatus.Cancelled)
            {
                await component.RespondAsync("This event is no longer available.", ephemeral: true);
                return;
            }

            // RSVPs close at start: re-render with locked buttons and inform the user.
            if (DateTime.UtcNow >= ev.StartUtc)
            {
                var current = await db.EventRsvps.Where(r => r.ClanEventId == clanEventId).ToListAsync();
                var lockedResolver = await EventEmbedBuilder.GuildNameResolverAsync(_client, ev, current);
                await component.UpdateAsync(m =>
                {
                    m.Embed      = EventEmbedBuilder.BuildEmbed(ev, current, ev.ImageFileName,
                        lockedResolver);
                    m.Components = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked: true);
                });
                return;
            }

            var existing = await db.EventRsvps
                .FirstOrDefaultAsync(r => r.ClanEventId == clanEventId && r.UserId == component.User.Id);
            if (existing is null)
                db.EventRsvps.Add(new EventRsvp
                {
                    ClanEventId = clanEventId,
                    UserId      = component.User.Id,
                    Status      = status.Value,
                    UpdatedAt   = DateTime.UtcNow,
                });
            else
            {
                // UpdatedAt is the waitlist's signup-order key, so only bump it on
                // a real intent change. Going and Waitlisted are the same "wants to
                // go" intent — re-clicking Going while waitlisted must NOT shove the
                // member behind people who signed up after them.
                var hadGoingIntent  = existing.Status is EventRsvpStatus.Going or EventRsvpStatus.Waitlisted;
                var wantsGoing      = status.Value == EventRsvpStatus.Going;
                var sameIntent      = existing.Status == status.Value || (wantsGoing && hadGoingIntent);

                existing.Status = status.Value;
                if (!sameIntent) existing.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();

            // Re-derive Going vs Waitlisted under the cap. This both demotes the
            // just-clicked member if they joined a full event and promotes the
            // oldest waitlister if this click freed a confirmed spot.
            var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == clanEventId).ToListAsync();
            var promoted = EventWaitlist.Rebalance(rsvps, ev.MaxParticipants);
            if (promoted.Count > 0 || db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync();

            var resolver = await EventEmbedBuilder.GuildNameResolverAsync(_client, ev, rsvps);
            await component.UpdateAsync(m =>
            {
                m.Embed      = EventEmbedBuilder.BuildEmbed(ev, rsvps, ev.ImageFileName,
                    resolver);
                m.Components = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked: false);
            });

            // Courtesy DMs to anyone auto-promoted off the waitlist by this click.
            foreach (var uid in promoted)
                await EventWaitlist.NotifyPromotedAsync(_client, ev, uid);

            // If the clicker asked to go but the cap was full, tell them quietly.
            if (status.Value == EventRsvpStatus.Going)
            {
                var mine = rsvps.FirstOrDefault(r => r.UserId == component.User.Id);
                if (mine?.Status == EventRsvpStatus.Waitlisted)
                    await component.FollowupAsync(
                        "🕓 This event is full, so you're on the **waitlist**. " +
                        "You'll be moved to Going automatically — and DM'd — if a spot opens.",
                        ephemeral: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSVP handling failed for event {ClanEventId}", clanEventId);
            try { await component.DeferAsync(); } catch { /* already acked */ }
        }
        finally
        {
            gate.Release();
        }
    }
}
