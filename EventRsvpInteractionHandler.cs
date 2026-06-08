using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

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

    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public EventRsvpInteractionHandler(
        IServiceProvider services,
        ILogger<EventRsvpInteractionHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
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

        var gate = _locks.GetOrAdd(clanEventId, _ => new SemaphoreSlim(1, 1));
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
                await component.UpdateAsync(m =>
                {
                    m.Embed      = EventEmbedBuilder.BuildEmbed(ev, current);
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
                existing.Status    = status.Value;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();

            var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == clanEventId).ToListAsync();
            await component.UpdateAsync(m =>
            {
                m.Embed      = EventEmbedBuilder.BuildEmbed(ev, rsvps);
                m.Components = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked: false);
            });
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
