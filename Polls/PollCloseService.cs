using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Background sweep that closes polls whose <see cref="Poll.ClosesAtUtc"/> has
/// passed and posts their winner announcement (via <see cref="PollClosingService"/>).
/// Runs on a short interval so closings are timely; the close itself is
/// idempotent, so a sweep overlapping a manual close or a restart is harmless.
/// </summary>
public sealed class PollCloseService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly PollClosingService _closer;
    private readonly ILogger<PollCloseService> _logger;

    public PollCloseService(
        IServiceProvider services,
        PollClosingService closer,
        ILogger<PollCloseService> logger)
    {
        _services = services;
        _closer   = closer;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll close sweep failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        List<int> due;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            due = await db.Polls
                .Where(p => p.Status == PollStatus.Open && p.ClosesAtUtc <= now)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var pollId in due)
        {
            ct.ThrowIfCancellationRequested();
            await _closer.CloseAsync(pollId, closedByUserId: null);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
