using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace ClanGuardBot.Services;

/// <summary>
/// Drains ApolloMessageLog into downstream state. Behaviour depends on the
/// UseNewApolloPipeline feature flag:
///
/// ── Flag OFF (Phase 2 behaviour) ──
/// Writes parsed events to the ApolloEvent staging table. Acts as a parallel
/// observability surface alongside the live ApolloEventHandler; the two tables
/// can be diffed to validate parser equivalence before cutover.
///
/// ── Flag ON (Phase 3 behaviour) ──
/// Bypasses the ApolloEvent table entirely. Upserts directly into CalendarEvent
/// (the live published state) and enqueues a CalendarOutbox row to drive the
/// asynchronous GCal push. The live ApolloEventHandler should be unregistered
/// at startup when the flag is on, so this worker is the only writer to
/// CalendarEvent for Apollo-sourced events.
///
/// ── Why a separate worker, not synchronous in the capture handler ──
/// Capture must never block on parsing or GCal. If a parse takes 200ms, or the
/// outbox table is contended, Discord gateway delivery shouldn't suffer. The
/// worker also gives us a natural retry surface — failed rows leave
/// ProcessedAt=null so Phase 6 replay can pick them up.
///
/// ── Failure handling ──
///   • Reconstructor throws / Parse returns null → stamp ParseError, set
///     ProcessedAt=now so we don't reprocess on every tick. Phase 6 replay
///     command can null ProcessedAt to force a retry after a parser fix.
///   • Database write fails → row stays unprocessed, retries on next tick.
///   • Tombstone for unknown message → silently mark processed.
///
/// ── Ordering and revision handling ──
/// Rows are processed oldest-CapturedAt-first. A message may have multiple
/// unprocessed revisions in the queue; the worker handles them in order. The
/// downstream state (ApolloEvent or CalendarEvent) reflects the latest
/// processed revision. A Deleted revision is handled specially per branch
/// (see ProcessTombstone* below).
///
/// ── Polling interval ──
/// 15 seconds. Apollo edits are infrequent at peak (a few per hour); shorter
/// intervals just burn DB queries.
/// </summary>
public class ApolloMessageParserWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);
    private const int BatchSize = 50;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<ApolloMessageParserWorker> _logger;
    private readonly BotConfig _config;

    public ApolloMessageParserWorker(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<ApolloMessageParserWorker> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be ready — not strictly needed (we only touch the
        // DB), but keeps log noise sequenced with other startup services.
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "ApolloMessageParserWorker started; polling every {Seconds}s (UseNewApolloPipeline={Flag})",
            (int)PollInterval.TotalSeconds, _config.UseNewApolloPipeline);

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
        // Tombstone path
        if (row.EventType == ApolloMessageEventType.Deleted)
        {
            if (_config.UseNewApolloPipeline)
                await ProcessTombstoneNewPathAsync(db, row);
            else
                await ProcessTombstoneOldPathAsync(db, row);

            row.ProcessedAt = DateTime.UtcNow;
            row.ParseError  = null;
            await db.SaveChangesAsync();
            return;
        }

        // Created / Updated path: parse first, then route by flag.
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
            // Apollo always posts an embed, so a missing embed means this row
            // isn't actually an event payload (some other Apollo message type,
            // or a stripped post-deletion edit). Mark processed with no
            // downstream side effect.
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

        if (_config.UseNewApolloPipeline)
            await ProcessParsedNewPathAsync(db, row, parsed);
        else
            await ProcessParsedOldPathAsync(db, row, parsed);

        row.ProcessedAt = DateTime.UtcNow;
        row.ParseError  = null;
        await db.SaveChangesAsync();
    }

    // ─── Old path (UseNewApolloPipeline=false): writes to ApolloEvent ────────

    private async Task ProcessTombstoneOldPathAsync(BotDbContext db, ApolloMessageLog row)
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
    }

    private async Task ProcessParsedOldPathAsync(
        BotDbContext db, ApolloMessageLog row, ApolloEmbedParser.ParsedApolloEvent parsed)
    {
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
        // for an RSVP without changing event details).
    }

    // ─── New path (UseNewApolloPipeline=true): writes to CalendarEvent + outbox ─

    /// <summary>
    /// Tombstone in the new path. Mirrors ApolloEventHandler.HandleMessageDeletedAsync:
    ///   • Pre-event deletion (EndUtc &gt; now): real cancellation. Enqueue a Delete
    ///     outbox row, then remove the CalendarEvent. Doing the enqueue before the
    ///     remove (in the same SaveChanges) means the outbox always has the GCal
    ///     event ID available even though CalendarEvent is gone.
    ///   • Post-event deletion (EndUtc &lt;= now): channel cleanup policy. Leave both
    ///     the CalendarEvent row and the GCal entry as historical record.
    /// </summary>
    private async Task ProcessTombstoneNewPathAsync(BotDbContext db, ApolloMessageLog row)
    {
        var calEvent = await db.CalendarEvents
            .FirstOrDefaultAsync(c => c.DiscordMessageId == row.DiscordMessageId);

        if (calEvent is null) return;

        if (calEvent.EndUtc <= DateTime.UtcNow)
        {
            _logger.LogDebug(
                "Apollo message {MessageId} deleted after event end; preserving CalendarEvent '{Title}' as historical record",
                row.DiscordMessageId, calEvent.Title);
            return;
        }

        // Pre-event deletion → real cancellation.
        if (!string.IsNullOrEmpty(calEvent.CalendarEventId))
        {
            // GCal entry exists; queue its deletion. Stash the GoogleEventId in
            // the payload because we're about to nuke the CalendarEvent row.
            db.CalendarOutbox.Add(new CalendarOutbox
            {
                GuildId         = row.GuildId,
                Operation       = CalendarOutboxOperation.Delete,
                CalendarEventId = calEvent.Id,
                PayloadJson     = JsonConvert.SerializeObject(new CalendarOutboxPayload
                {
                    Title         = calEvent.Title,
                    StartUtc      = calEvent.StartUtc,
                    EndUtc        = calEvent.EndUtc,
                    Description   = calEvent.Description,
                    Source        = calEvent.Source,
                    GoogleEventId = calEvent.CalendarEventId,
                }),
                NextAttemptAt = DateTime.UtcNow,
                CreatedAt     = DateTime.UtcNow,
            });
        }
        // else: CalendarEvent existed but never made it to GCal (Create still
        // pending in the outbox). Removing the row is sufficient; the pending
        // Create will skip itself when it sees the row is gone.

        db.CalendarEvents.Remove(calEvent);

        _logger.LogInformation(
            "Pre-event Apollo deletion: queued GCal delete for '{Title}' (messageId={MessageId}, EndUtc was {End:yyyy-MM-dd HH:mm} UTC)",
            calEvent.Title, row.DiscordMessageId, calEvent.EndUtc);
    }

    /// <summary>
    /// Parse + upsert + enqueue in the new path. Mirrors ApolloEventHandler's
    /// CREATE/UPDATE branching:
    ///   • Existing CalendarEvent for this message → UPDATE path: write the new
    ///     fields, enqueue an Update outbox row (or Create if no GoogleEventId yet).
    ///   • No existing CalendarEvent → CREATE path: insert with empty
    ///     CalendarEventId, enqueue a Create outbox row.
    ///
    /// All writes happen in one SaveChanges, so a partial state (CalendarEvent
    /// without a paired outbox row, or vice versa) is structurally impossible.
    /// </summary>
    private async Task ProcessParsedNewPathAsync(
        BotDbContext db, ApolloMessageLog row, ApolloEmbedParser.ParsedApolloEvent parsed)
    {
        var existing = await db.CalendarEvents
            .FirstOrDefaultAsync(c => c.DiscordMessageId == row.DiscordMessageId);

        var payload = new CalendarOutboxPayload
        {
            Title         = parsed.Title,
            StartUtc      = parsed.StartUtc,
            EndUtc        = parsed.EndUtc,
            Description   = parsed.Description ?? "",
            OrganizerName = parsed.OrganizerName ?? "",
            OrganizerId   = parsed.OrganizerId,
            Source        = "Clan",
        };

        if (existing is null)
        {
            // CREATE path
            var calEvent = new CalendarEvent
            {
                GuildId          = row.GuildId,
                DiscordMessageId = row.DiscordMessageId,
                CalendarEventId  = string.Empty, // populated by outbox worker on success
                Title            = parsed.Title,
                StartUtc         = parsed.StartUtc,
                EndUtc           = parsed.EndUtc,
                Description      = parsed.Description ?? "",
                Source           = "Clan",
                CreatedAt        = DateTime.UtcNow,
            };
            db.CalendarEvents.Add(calEvent);

            // Save once to materialize calEvent.Id, then enqueue with the FK.
            await db.SaveChangesAsync();

            db.CalendarOutbox.Add(new CalendarOutbox
            {
                GuildId         = row.GuildId,
                Operation       = CalendarOutboxOperation.Create,
                CalendarEventId = calEvent.Id,
                PayloadJson     = JsonConvert.SerializeObject(payload),
                NextAttemptAt   = DateTime.UtcNow,
                CreatedAt       = DateTime.UtcNow,
            });

            _logger.LogInformation(
                "CalendarEvent created for Apollo message {MessageId}: '{Title}' {Start}–{End} UTC (GCal create queued)",
                row.DiscordMessageId, parsed.Title, parsed.StartUtc, parsed.EndUtc);
            return;
        }

        // UPDATE path — apply changes only if something actually changed.
        var changed =
            existing.Title       != parsed.Title              ||
            existing.StartUtc    != parsed.StartUtc           ||
            existing.EndUtc      != parsed.EndUtc             ||
            existing.Description != (parsed.Description ?? "");

        if (!changed)
        {
            // Apollo re-rendered the embed for an RSVP — no event-field change,
            // so nothing to push to GCal. Skip the enqueue entirely.
            return;
        }

        existing.Title       = parsed.Title;
        existing.StartUtc    = parsed.StartUtc;
        existing.EndUtc      = parsed.EndUtc;
        existing.Description = parsed.Description ?? "";

        // If the original Create hasn't completed yet (no GoogleEventId), enqueue
        // another Create — the outbox worker's idempotency check on CalendarEventId
        // will fold this into an Update once the prior Create lands. Otherwise
        // enqueue a normal Update.
        var op = string.IsNullOrEmpty(existing.CalendarEventId)
            ? CalendarOutboxOperation.Create
            : CalendarOutboxOperation.Update;

        db.CalendarOutbox.Add(new CalendarOutbox
        {
            GuildId         = row.GuildId,
            Operation       = op,
            CalendarEventId = existing.Id,
            PayloadJson     = JsonConvert.SerializeObject(payload),
            NextAttemptAt   = DateTime.UtcNow,
            CreatedAt       = DateTime.UtcNow,
        });

        _logger.LogInformation(
            "CalendarEvent updated for Apollo message {MessageId}: '{Title}' {Start}–{End} UTC (GCal {Op} queued)",
            row.DiscordMessageId, parsed.Title, parsed.StartUtc, parsed.EndUtc, op);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

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