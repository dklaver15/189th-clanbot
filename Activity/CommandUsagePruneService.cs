using ClanGuardBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Background service that prunes CommandUsages rows older than the
/// configured retention window (90 days). Runs once at startup, then
/// once every 24h while the host is alive.
///
/// ── Why a separate service ──
/// CommandUsageTrackingHandler is on the Discord-event hot path; we don't
/// want EF deletes happening there. A dedicated daily sweep keeps the
/// table from growing unbounded without coupling write latency to
/// retention. The IX_CommandUsages_ExecutedAt index makes the cutoff
/// query a cheap range scan even after a year of data.
///
/// ── Why nightly is fine ──
/// Retention is a soft target. A row that lives one extra day past 90
/// because the sweep hasn't fired yet doesn't matter; the goal is
/// bounded growth, not millisecond-accurate eviction.
/// </summary>
public class CommandUsagePruneService : BackgroundService
{
    private static readonly TimeSpan Retention     = TimeSpan.FromDays(90);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    private readonly IServiceProvider _services;
    private readonly ILogger<CommandUsagePruneService> _logger;

    public CommandUsagePruneService(
        IServiceProvider services,
        ILogger<CommandUsagePruneService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CommandUsage prune sweep failed");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - Retention;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var deleted = await db.CommandUsages
            .Where(c => c.ExecutedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "CommandUsage prune: deleted {Count} rows older than {Cutoff:u}",
                deleted, cutoff);
        }
    }
}
