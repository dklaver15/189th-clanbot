using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Keeps recurring series filled out over a rolling horizon. Each tick, for
/// every active <see cref="ClanEventSeries"/>, it materializes any occurrences
/// now inside the EventRecurrenceHorizonDays window (capped per pass by
/// EventRecurrenceMaxBackfill) via <see cref="EventPublisher.CreateOccurrenceAsync"/>,
/// which is idempotent on (SeriesId, StartUtc) — so already-created and
/// already-cancelled occurrences are never duplicated.
///
/// Recurrence dates come from <see cref="ClanEventRecurrence"/>, which anchors
/// each occurrence in the organizer's local wall-clock for DST safety.
/// </summary>
public sealed class EventRecurrenceScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(40);

    private readonly IServiceProvider _services;
    private readonly EventPublisher _publisher;
    private readonly BotConfig _config;
    private readonly ILogger<EventRecurrenceScheduler> _logger;

    public EventRecurrenceScheduler(
        IServiceProvider services,
        EventPublisher publisher,
        IOptions<BotConfig> config,
        ILogger<EventRecurrenceScheduler> logger)
    {
        _services  = services;
        _publisher = publisher;
        _config    = config.Value;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "EventRecurrenceScheduler started; polling every {Minutes}m (horizon={Horizon}d, maxBackfill={Max})",
            (int)PollInterval.TotalMinutes, _config.EventRecurrenceHorizonDays, _config.EventRecurrenceMaxBackfill);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventRecurrenceScheduler tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Load active series and retire any whose end has passed, in one scope.
        List<ClanEventSeries> active;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var all = await db.ClanEventSeries.Where(s => s.Active).ToListAsync(ct);

            active = new List<ClanEventSeries>();
            var retired = false;
            foreach (var s in all)
            {
                if (s.UntilUtc is DateTime u && u < now)
                {
                    s.Active = false;       // series window has closed
                    retired = true;
                }
                else
                {
                    active.Add(s);
                }
            }
            if (retired) await db.SaveChangesAsync(ct);
        }

        if (active.Count == 0) return;

        // Materialize via the publisher (each call opens its own scope and is
        // idempotent), so we don't hold a DbContext across Discord I/O.
        // FillHorizonAsync also carries the series banner onto each new occurrence.
        foreach (var series in active)
        {
            if (ct.IsCancellationRequested) break;
            await _publisher.FillHorizonAsync(series);
        }
    }
}
