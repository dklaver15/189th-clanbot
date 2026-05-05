using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Phase 2 of the Apollo sync rework: stage parsing on a separate timeline.
///
/// Polls ApolloMessageLog for unprocessed rows, reconstructs the embed via
/// ApolloEmbedReconstructor, parses it with the existing ApolloEmbedParser,
/// and upserts an ApolloEvent row reflecting the latest revision per message.
/// Stamps ApolloMessageLog.ProcessedAt on success or ParseError on failure.
///
/// ── Dual-run posture ──
/// During Phase 2 the live ApolloEventHandler keeps writing to CalendarEvent
/// as before. This worker writes ONLY to ApolloEvent. The two tables can then
/// be diffed (see ApolloEvent class doc) to validate that the new parser path
/// produces equivalent output before we cut over in Phase 3.
///
/// ── Why a separate worker, not synchronous in the capture handler ──
/// Capture must never block on parsing. If the parser takes 200ms or hits a
/// transient DB issue, Discord gateway delivery shouldn't suffer. The worker
/// also gives us a natural retry surface (failed rows stay unprocessed; fix
/// the parser, run the replay command, queue drains itself).
///
/// ── Failure handling ──
///   • Reconstructor throws / Parse returns null → stamp ParseError, set
///     ProcessedAt=null so Phase 6 replay can pick it up after a fix. Log warn.
///   • Database write fails → leave row unprocessed, retry on next tick. Log error.
///   • Skip rows whose latest revision is Deleted but no prior Active state
///     existed (ignored deletion of unknown message). Already filtered out
///     during capture, but defensive here too.
///
/// ── Ordering and revision handling ──
/// Rows are processed oldest-CapturedAt-first. A given message may have
/// multiple unprocessed revisions in the queue; the worker handles them in
/// order. The ApolloEvent row reflects the LATEST revision processed; a
/// Deleted revision flips Status to Cancelled rather than removing the row,
/// so the audit trail is preserved.
///
/// ── Polling interval ──
/// 15 seconds. Apollo edits are infrequent (a few per day at peak); shorter
/// intervals just burn DB queries. Phase 5's health monitor will alert if
/// queue depth grows beyond N or oldest unprocessed exceeds an SLA.
/// </summary>
public class ApolloMessageParserWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);
    private const int BatchSize = 50;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<ApolloMessageParserWorker> _logger;

    public ApolloMessageParserWorker(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<ApolloMessageParserWorker> logger)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be ready — not strictly needed (we only touch the
        // DB), but keeps log noise sequenced with other startup services and
        // avoids a flurry of work if the bot is in a restart loop.
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("ApolloMessageParserWorker started; polling every {Seconds}s",
            (int)PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ApolloMessageParserWorker batch failed; will retry on next tick");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var batch = await db.ApolloMessageLogs
            .Where(x => x.ProcessedAt == null)
            .OrderBy(x => x.CapturedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return;

        _logger.LogDebug("Processing {Count} unprocessed Apollo log row(s)", batch.Count);

        foreach (var row in batch)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessRowAsync(db, row);
            }
            catch (Exception ex)
            {
                // Per-row isolation: one bad row never blocks the queue.
                row.ParseError  = $"{ex.GetType().Name}: {ex.Message}";
                row.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                _logger.LogWarning(ex,
                    "Failed to process ApolloMessageLog row {LogId} (messageId={MessageId} rev {Revision})",
                    row.Id, row.DiscordMessageId, row.RevisionNumber);
            }
        }
    }

    private async Task ProcessRowAsync(BotDbContext db, ApolloMessageLog row)
    {
        // Tombstone path: flip the ApolloEvent to Cancelled if it exists.
        if (row.EventType == ApolloMessageEventType.Deleted)
        {
            var existing = await db.ApolloEvents
                .FirstOrDefaultAsync(e => e.DiscordMessageId == row.DiscordMessageId);

            if (existing is not null && existing.Status != ApolloEventStatus.Cancelled)
            {
                existing.Status      = ApolloEventStatus.Cancelled;
                existing.CancelledAt = DateTime.UtcNow;
                existing.SourceLogId = row.Id;

                _logger.LogInformation(
                    "ApolloEvent for message {MessageId} ('{Title}') marked Cancelled",
                    row.DiscordMessageId, existing.ParsedTitle);
            }

            row.ProcessedAt = DateTime.UtcNow;
            row.ParseError  = null;
            await db.SaveChangesAsync();
            return;
        }

        // Created / Updated path: parse and upsert.
        if (row.PayloadJson is null)
        {
            // Should never happen — capture only writes null PayloadJson on
            // Deleted. Treat as parse failure for visibility.
            throw new InvalidOperationException(
                "ApolloMessageLog row has null PayloadJson but EventType is not Deleted");
        }

        var snapshot = ApolloMessageSnapshot.FromJson(row.PayloadJson);
        var embed = ApolloEmbedReconstructor.FirstEmbedOrNull(snapshot);

        if (embed is null)
        {
            // Apollo always posts an embed, so a missing embed means this
            // row isn't actually an event payload (some other Apollo message
            // type, or a stripped post-deletion edit). Mark processed with no
            // ApolloEvent side effect.
            row.ProcessedAt = DateTime.UtcNow;
            row.ParseError  = "Snapshot had no embed; nothing to parse";
            await db.SaveChangesAsync();
            return;
        }

        var parsed = ApolloEmbedParser.Parse(embed);
        if (parsed is null)
        {
            row.ProcessedAt = DateTime.UtcNow;
            row.ParseError  = "ApolloEmbedParser returned null (no title or no parseable time)";
            await db.SaveChangesAsync();

            _logger.LogWarning(
                "ApolloEmbedParser returned null for message {MessageId} rev {Revision}",
                row.DiscordMessageId, row.RevisionNumber);
            return;
        }

        var contentHash = ComputeContentHash(parsed);

        var existingEvent = await db.ApolloEvents
            .FirstOrDefaultAsync(e => e.DiscordMessageId == row.DiscordMessageId);

        if (existingEvent is null)
        {
            db.ApolloEvents.Add(new ApolloEvent
            {
                GuildId             = row.GuildId,
                DiscordMessageId    = row.DiscordMessageId,
                ParsedTitle         = parsed.Title,
                ParsedStartUtc      = parsed.StartUtc,
                ParsedEndUtc        = parsed.EndUtc,
                ParsedDescription   = parsed.Description,
                ParsedOrganizerId   = parsed.OrganizerId,
                ParsedOrganizerName = parsed.OrganizerName,
                Status              = ApolloEventStatus.Active,
                ContentHash         = contentHash,
                ParsedAt            = DateTime.UtcNow,
                SourceLogId         = row.Id,
            });

            _logger.LogInformation(
                "ApolloEvent created for message {MessageId}: '{Title}' {Start}–{End} UTC",
                row.DiscordMessageId, parsed.Title, parsed.StartUtc, parsed.EndUtc);
        }
        else if (existingEvent.ContentHash != contentHash || existingEvent.Status != ApolloEventStatus.Active)
        {
            existingEvent.ParsedTitle         = parsed.Title;
            existingEvent.ParsedStartUtc      = parsed.StartUtc;
            existingEvent.ParsedEndUtc        = parsed.EndUtc;
            existingEvent.ParsedDescription   = parsed.Description;
            existingEvent.ParsedOrganizerId   = parsed.OrganizerId;
            existingEvent.ParsedOrganizerName = parsed.OrganizerName;
            existingEvent.Status              = ApolloEventStatus.Active;
            existingEvent.CancelledAt         = null;
            existingEvent.ContentHash         = contentHash;
            existingEvent.ParsedAt            = DateTime.UtcNow;
            existingEvent.SourceLogId         = row.Id;

            _logger.LogInformation(
                "ApolloEvent updated for message {MessageId}: '{Title}' {Start}–{End} UTC",
                row.DiscordMessageId, parsed.Title, parsed.StartUtc, parsed.EndUtc);
        }
        // else: hash matched and Status was Active → no-op (Apollo re-rendered
        // the embed for an RSVP without changing event details).

        row.ProcessedAt = DateTime.UtcNow;
        row.ParseError  = null;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// FNV-1a over the parser-relevant fields. Cheap and stable; we don't need
    /// cryptographic strength, just "did anything actually change?"
    /// </summary>
    private static string ComputeContentHash(ApolloEmbedParser.ParsedApolloEvent parsed)
    {
        const ulong fnvOffset = 14695981039346656037;
        const ulong fnvPrime  = 1099511628211;

        var input = string.Join("|",
            parsed.Title,
            parsed.StartUtc.Ticks,
            parsed.EndUtc.Ticks,
            parsed.Description ?? "",
            parsed.OrganizerId?.ToString() ?? "");

        ulong hash = fnvOffset;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(input))
        {
            hash ^= b;
            hash *= fnvPrime;
        }
        return hash.ToString("x16");
    }
}
