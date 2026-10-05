using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// One-shot startup service that seeds the <see cref="RankChange"/> table
/// from existing <see cref="RankHistory"/> rows on first deploy of the
/// rank-history-chain feature. After the first successful run there is
/// nothing to backfill on subsequent startups, so the service is
/// effectively a no-op after deploy day.
///
/// ── Why no BotState flag ──
/// Idempotency is enforced structurally rather than with a state flag:
/// the SELECT query returns only rows that don't already have a matching
/// "Initial" entry (FromRank IS NULL) in <see cref="RankChange"/>. After
/// the first run that writes every needed row, the query is empty and the
/// foreach is a no-op. The cost is one indexed query per startup — far
/// cheaper than the BotState column + read it would replace, and immune
/// to "did the state flag get accidentally cleared?" failure modes.
///
/// ── Why hosted service, not migration ──
/// EF migrations can't write data that depends on other tables in a way
/// that survives a fresh-database deploy. A hosted service running after
/// <c>Database.MigrateAsync</c> sees the schema in place and can do the
/// in-app work cleanly. This also keeps the migration files purely
/// schema-shaped, matching the rest of the codebase's pattern.
///
/// ── Backfilled timestamp ──
/// Each backfill row writes ChangedAt = source RankHistory.AssignedAt, so
/// the seeded "Initial" entry lands chronologically where the current
/// rank's assignment actually happened. For members whose current rank
/// was assigned long before this feature deploys, that timestamp may be
/// months old — that's correct; the chain just doesn't have entries for
/// the unobserved earlier promotions.
///
/// ── Failure mode ──
/// Any exception during backfill is logged but does not throw. The bot
/// continues to start; the next restart retries. Partial progress is
/// safe because each row is independent and the dedup guard prevents
/// double-writing.
/// </summary>
public class RankChangeBackfillService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RankChangeBackfillService> _logger;

    public RankChangeBackfillService(
        IServiceProvider services,
        ILogger<RankChangeBackfillService> logger)
    {
        _services = services;
        _logger   = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Single indexed query: find RankHistory rows that have no
            // corresponding "Initial" (FromRank IS NULL) entry in RankChange.
            // After the first successful run, this list is empty and the
            // method returns having done effectively zero work.
            var toBackfill = await (
                from rh in db.RankHistories.AsNoTracking()
                where !db.RankChanges.Any(c =>
                    c.GuildId == rh.GuildId
                    && c.UserId == rh.UserId
                    && c.FromRank == null)
                select new
                {
                    rh.GuildId,
                    rh.UserId,
                    rh.RankName,
                    rh.AssignedAt,
                }
            ).ToListAsync(cancellationToken);

            if (toBackfill.Count == 0)
            {
                _logger.LogInformation(
                    "RankChange backfill: nothing to do (every RankHistory row already has an Initial entry).");
                return;
            }

            foreach (var row in toBackfill)
            {
                db.RankChanges.Add(new RankChange
                {
                    GuildId   = row.GuildId,
                    UserId    = row.UserId,
                    FromRank  = null,
                    ToRank    = row.RankName,
                    ChangedAt = row.AssignedAt,
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "RankChange backfill complete: seeded {Count} Initial entries from RankHistory.",
                toBackfill.Count);
        }
        catch (Exception ex)
        {
            // Don't block bot startup on backfill failure. Next restart retries.
            _logger.LogError(ex,
                "RankChange backfill failed; bot will continue starting. Will retry on next restart.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
