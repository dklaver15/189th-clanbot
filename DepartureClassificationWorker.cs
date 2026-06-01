using ClanGuardBot.Data;
using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Retention pipeline finalizer. Two jobs, both cheap, on a short poll:
///
///   1. Any MemberDeparture still "Pending" and older than the grace window
///      is finalized to "Left" — no Kick/Ban audit entry showed up in time,
///      so the departure was voluntary. The deterministic settling step of
///      the capture → reconcile → grace-finalize pipeline, same shape as the
///      Apollo /sort rebind grace window.
///
///   2. Evicts stale breadcrumbs from DepartureCaptureHandler's in-memory
///      correlation cache (Kick/Ban audit entries that never matched a leave,
///      e.g. banning someone who had already left long ago).
///
/// ── Why a worker and not inline ──
/// "No audit entry arrived" is the absence of an event — you can only conclude
/// it by waiting. A timer is the natural mechanism. Mirrors CalendarOutboxWorker.
/// </summary>
public class DepartureClassificationWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly DepartureCaptureHandler _captureHandler;
    private readonly ILogger<DepartureClassificationWorker> _logger;
    private readonly BotConfig _config;

    public DepartureClassificationWorker(
        IServiceProvider services,
        DepartureCaptureHandler captureHandler,
        ILogger<DepartureClassificationWorker> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _captureHandler = captureHandler;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "DepartureClassificationWorker started; polling every {Seconds}s (grace {Grace}s)",
            (int)PollInterval.TotalSeconds, _config.RetentionClassificationGraceSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FinalizeAsync(stoppingToken);
                _captureHandler.EvictStalePendingKicks(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DepartureClassificationWorker tick failed; will retry next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task FinalizeAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - _captureHandler.GraceWindow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var stale = await db.MemberDepartures
            .Where(d => d.Classification == "Pending" && d.CreatedAt < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        var now = DateTime.UtcNow;
        foreach (var row in stale)
        {
            row.Classification = "Left";
            row.ClassifiedAt = now;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Finalized {Count} departure(s) as voluntary 'Left' (grace window elapsed)",
            stale.Count);
    }
}
