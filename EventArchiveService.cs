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
/// Auto-archives ClanGuard-created events: EventArchiveDelayMinutes after an
/// event ends, its #events post is deleted and the ClanEvent is marked
/// Archived. The CalendarEvent row and Google Calendar entry are kept — this
/// removes only the Discord message.
///
/// ── Scope ──
/// Operates solely on the ClanEvents table, which only ever holds
/// ClanGuard-authored events, so Apollo posts are never affected. Runs well
/// after the attendance snapshot (which reads voice sessions, not the message),
/// so promotion credit is unaffected. The archived row remains, so the
/// recurrence scheduler still treats that occurrence's slot as filled.
/// </summary>
public sealed class EventArchiveService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<EventArchiveService> _logger;

    public EventArchiveService(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<EventArchiveService> logger)
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
            "EventArchiveService started; polling every {Minutes}m (enabled={Enabled}, delay={Delay}m)",
            (int)PollInterval.TotalMinutes, _config.EventAutoArchiveEnabled, _config.EventArchiveDelayMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.EventAutoArchiveEnabled)
                    await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventArchiveService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-_config.EventArchiveDelayMinutes);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var due = await db.ClanEvents
            .Where(e => e.Status == ClanEventStatus.Scheduled && e.EndUtc <= cutoff)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        var archived = 0;
        foreach (var ev in due)
        {
            if (ct.IsCancellationRequested) break;

            await DeletePostAsync(ev);
            ev.Status = ClanEventStatus.Archived; // keep the row + CalendarEvent + GCal entry
            await db.SaveChangesAsync(ct);        // persist per-item: a crash mid-loop won't re-delete already-archived posts
            archived++;
        }

        if (archived > 0)
            _logger.LogInformation("Auto-archived {Count} event post(s)", archived);
    }

    private async Task DeletePostAsync(ClanEvent ev)
    {
        try
        {
            if (_client.GetChannel(ev.ChannelId) is IMessageChannel channel)
                await channel.DeleteMessageAsync(ev.MessageId);
        }
        catch (Exception ex)
        {
            // Already deleted, or missing perms — mark archived anyway so we
            // don't retry forever; log so a perms issue is visible.
            _logger.LogWarning(ex, "Could not delete post for event {ClanId} (msg {MsgId})", ev.Id, ev.MessageId);
        }
    }
}
