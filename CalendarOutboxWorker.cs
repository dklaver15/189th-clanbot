using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace ClanGuardBot.Services;

/// <summary>
/// Phase 3 of the Apollo sync rework: drains the CalendarOutbox table to
/// Google Calendar with row-level exponential backoff.
///
/// ── Pipeline shape (with UseNewApolloPipeline=true) ──
///   Apollo post → MessageReceived
///       → ApolloMessageCaptureHandler writes ApolloMessageLog row
///       → ApolloMessageParserWorker parses, upserts CalendarEvent, enqueues CalendarOutbox
///       → CalendarOutboxWorker (this class) drains CalendarOutbox → GCal
///       → on Create/Update success, runs overlap check + organizer DM
///
/// ── Why an outbox ──
/// Pre-Phase-3, ApolloEventHandler called GoogleCalendarService synchronously
/// inside the gateway-event callback. Transient GCal failures (5xx, throttling,
/// auth-token refresh blip) became silent drift — the DB row got written, the
/// API call didn't, and the inconsistency only surfaced when /cleanup-calendar-dupes
/// ran. With the outbox, a failed GCal call is just an incremented AttemptCount
/// and a deferred NextAttemptAt; it gets retried until it sticks.
///
/// ── Idempotency ──
/// Create operations check CalendarEvent.CalendarEventId before calling GCal.
/// If it's already populated (backfill won the race, or a previous worker run
/// completed the API call but crashed before stamping CompletedAt), the create
/// is auto-converted to an update so we don't write a duplicate GCal event.
///
/// ── Backoff schedule ──
///   Attempt 1 fails → retry in 30s
///   Attempt 2 fails → retry in 2 min
///   Attempt 3 fails → retry in 10 min
///   Attempt 4 fails → retry in 30 min
///   Attempt 5+ fails → retry every 1 hr, indefinitely (Phase 5 monitor alerts)
///
/// ── Feature flag ──
/// When UseNewApolloPipeline=false, the worker still ticks but the outbox will
/// be empty (parser worker only enqueues when the flag is on). Effectively a
/// no-op cost of one indexed SELECT every 10 seconds.
/// </summary>
public class CalendarOutboxWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(25);
    private const int BatchSize = 25;

    // Per-attempt backoff schedule. Last entry repeats forever once exceeded.
    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    ];

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<CalendarOutboxWorker> _logger;
    private readonly BotConfig _config;

    public CalendarOutboxWorker(
        IServiceProvider services,
        DiscordSocketClient client,
        GoogleCalendarService calendarService,
        ILogger<CalendarOutboxWorker> logger,
        IOptions<BotConfig> config)
    {
        _services        = services;
        _client          = client;
        _calendarService = calendarService;
        _logger          = logger;
        _config          = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait long enough that the Discord client is ready and the backfill
        // service has had a chance to start — backfill writes directly to
        // CalendarEvents + GCal, and we'd rather let it finish populating
        // CalendarEventId values before the outbox tries to "create" anything
        // it claims is missing.
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "CalendarOutboxWorker started; polling every {Seconds}s (UseNewApolloPipeline={Flag})",
            (int)PollInterval.TotalSeconds, _config.UseNewApolloPipeline);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CalendarOutboxWorker batch failed; will retry on next tick");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var batch = await db.CalendarOutbox
            .Where(o => o.CompletedAt == null && o.NextAttemptAt <= now)
            .OrderBy(o => o.NextAttemptAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return;

        _logger.LogDebug("CalendarOutboxWorker: processing {Count} pending row(s)", batch.Count);

        foreach (var row in batch)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessRowAsync(db, row, ct);
        }
    }

    private async Task ProcessRowAsync(BotDbContext db, CalendarOutbox row, CancellationToken ct)
    {
        CalendarOutboxPayload? payload;
        try
        {
            payload = JsonConvert.DeserializeObject<CalendarOutboxPayload>(row.PayloadJson);
            if (payload is null)
                throw new InvalidOperationException("Payload deserialized to null");
        }
        catch (Exception ex)
        {
            // A malformed payload can't be retried — corrupted at enqueue.
            // Stamp it complete with the error so it stops blocking the queue,
            // and surface a loud warning so we notice if it happens at scale.
            row.LastError   = $"Payload deserialize failed: {ex.Message}";
            row.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogError(ex,
                "CalendarOutbox row {RowId} has unparseable PayloadJson; marking complete with error",
                row.Id);
            return;
        }

        try
        {
            switch (row.Operation)
            {
                case CalendarOutboxOperation.Create:
                    await HandleCreateAsync(db, row, payload, ct);
                    break;

                case CalendarOutboxOperation.Update:
                    await HandleUpdateAsync(db, row, payload, ct);
                    break;

                case CalendarOutboxOperation.Delete:
                    await HandleDeleteAsync(db, row, payload, ct);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown CalendarOutboxOperation: {row.Operation}");
            }
        }
        catch (Exception ex)
        {
            await MarkFailedAsync(db, row, ex, ct);
        }
    }

    // ─── Operation handlers ──────────────────────────────────────────────────

    private async Task HandleCreateAsync(
        BotDbContext db, CalendarOutbox row, CalendarOutboxPayload payload, CancellationToken ct)
    {
        var calEvent = await db.CalendarEvents
            .FirstOrDefaultAsync(c => c.Id == row.CalendarEventId, ct);

        if (calEvent is null)
        {
            // The CalendarEvent was deleted between enqueue and processing
            // (e.g. cancellation came in fast). Drop the Create silently.
            _logger.LogInformation(
                "CalendarOutbox Create row {RowId}: CalendarEvent {EventId} no longer exists; skipping",
                row.Id, row.CalendarEventId);
            row.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        // Idempotency: if CalendarEventId is already populated, somebody got
        // there first (backfill, or a previous worker run that completed the
        // API call but crashed before stamping). Fold into an Update.
        if (!string.IsNullOrEmpty(calEvent.CalendarEventId))
        {
            _logger.LogInformation(
                "CalendarOutbox Create row {RowId}: CalendarEvent already has GoogleEventId={GoogleId}; converting to Update",
                row.Id, calEvent.CalendarEventId);

            var updated = await _calendarService.UpdateEventAsync(
                calEvent.CalendarEventId,
                payload.Title,
                payload.StartUtc,
                payload.EndUtc,
                description: payload.Description);

            if (updated is null)
                throw new InvalidOperationException(
                    $"GCal update returned null for event {calEvent.CalendarEventId}");

            row.CompletedAt = DateTime.UtcNow;
            row.LastError   = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        var created = await _calendarService.CreateEventAsync(
            title:       payload.Title,
            startUtc:    payload.StartUtc,
            endUtc:      payload.EndUtc,
            description: payload.Description,
            creatorName: payload.OrganizerName,
            source:      payload.Source);

        calEvent.CalendarEventId = created.Id;
        row.CompletedAt          = DateTime.UtcNow;
        row.LastError            = null;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CalendarOutbox Create row {RowId}: created GCal event {GoogleId} for '{Title}' {Start:yyyy-MM-dd HH:mm}–{End:HH:mm} UTC",
            row.Id, created.Id, payload.Title, payload.StartUtc, payload.EndUtc);

        // Overlap detection + organizer DM happens AFTER the create lands in
        // GCal — that way the message we send the organizer is truthful
        // ("the event is on the calendar; here are the conflicts").
        await TrySendOverlapDmAsync(row, payload, calEvent.CalendarEventId, isReschedule: false);
    }

    private async Task HandleUpdateAsync(
        BotDbContext db, CalendarOutbox row, CalendarOutboxPayload payload, CancellationToken ct)
    {
        var calEvent = await db.CalendarEvents
            .FirstOrDefaultAsync(c => c.Id == row.CalendarEventId, ct);

        if (calEvent is null)
        {
            _logger.LogInformation(
                "CalendarOutbox Update row {RowId}: CalendarEvent {EventId} no longer exists; skipping",
                row.Id, row.CalendarEventId);
            row.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (string.IsNullOrEmpty(calEvent.CalendarEventId))
        {
            // The original Create hasn't landed yet — this Update is racing
            // ahead of it. Defer; the queue ordering on NextAttemptAt will
            // naturally serialize them once the Create completes.
            _logger.LogInformation(
                "CalendarOutbox Update row {RowId}: CalendarEvent has no GoogleEventId yet; deferring 30s",
                row.Id);
            row.AttemptCount  += 1;
            row.NextAttemptAt  = DateTime.UtcNow.AddSeconds(30);
            row.LastError      = "Awaiting CalendarEvent.CalendarEventId from prior Create";
            await db.SaveChangesAsync(ct);
            return;
        }

        var updated = await _calendarService.UpdateEventAsync(
            calEvent.CalendarEventId,
            payload.Title,
            payload.StartUtc,
            payload.EndUtc,
            description: payload.Description);

        if (updated is null)
            throw new InvalidOperationException(
                $"GCal update returned null for event {calEvent.CalendarEventId}");

        row.CompletedAt = DateTime.UtcNow;
        row.LastError   = null;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CalendarOutbox Update row {RowId}: updated GCal event {GoogleId} for '{Title}'",
            row.Id, calEvent.CalendarEventId, payload.Title);

        // Only DM on updates that actually moved the event in time. Title and
        // description edits can't create a conflict.
        if (Math.Abs((calEvent.StartUtc - payload.StartUtc).TotalMinutes) >= 1 ||
            Math.Abs((calEvent.EndUtc   - payload.EndUtc  ).TotalMinutes) >= 1)
        {
            await TrySendOverlapDmAsync(row, payload, calEvent.CalendarEventId, isReschedule: true);
        }
    }

    private async Task HandleDeleteAsync(
        BotDbContext db, CalendarOutbox row, CalendarOutboxPayload payload, CancellationToken ct)
    {
        // For Delete the GoogleEventId travels in the payload because the
        // CalendarEvent row has already been removed by the parser worker at
        // enqueue time. (Deleting CalendarEvent first lets EventAttendance's
        // FK-less reference stay coherent.)
        if (string.IsNullOrEmpty(payload.GoogleEventId))
        {
            _logger.LogWarning(
                "CalendarOutbox Delete row {RowId} has no GoogleEventId in payload; marking complete",
                row.Id);
            row.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        await _calendarService.DeleteEventAsync(payload.GoogleEventId);

        row.CompletedAt = DateTime.UtcNow;
        row.LastError   = null;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CalendarOutbox Delete row {RowId}: deleted GCal event {GoogleId} ('{Title}')",
            row.Id, payload.GoogleEventId, payload.Title);
    }

    // ─── Failure path ────────────────────────────────────────────────────────

    private async Task MarkFailedAsync(
        BotDbContext db, CalendarOutbox row, Exception ex, CancellationToken ct)
    {
        row.AttemptCount += 1;
        row.LastError     = $"{ex.GetType().Name}: {ex.Message}";

        // Pick the backoff slot for this attempt; clamp to the last entry.
        var slot = Math.Min(row.AttemptCount - 1, BackoffSchedule.Length - 1);
        row.NextAttemptAt = DateTime.UtcNow.Add(BackoffSchedule[slot]);

        await db.SaveChangesAsync(ct);

        // Log loudly after a few retries — by attempt 4 (~40 min in) this
        // genuinely deserves attention even pre-Phase-5 monitoring.
        var logLevel = row.AttemptCount >= 4 ? LogLevel.Error : LogLevel.Warning;
        _logger.Log(logLevel, ex,
            "CalendarOutbox row {RowId} ({Op}) failed (attempt {Attempt}); next attempt {NextAt:yyyy-MM-dd HH:mm} UTC",
            row.Id, row.Operation, row.AttemptCount, row.NextAttemptAt);
    }

    // ─── Overlap DM (post-write) ─────────────────────────────────────────────

    /// <summary>
    /// After a Create or Update lands in GCal, check for overlapping events
    /// and DM the organizer if there are conflicts.
    ///
    /// This is the post-Phase-3 location for what ApolloEventHandler used to do
    /// inline. Catching exceptions liberally because a missed DM is way less
    /// bad than failing the queue row over a Discord API hiccup.
    /// </summary>
    private async Task TrySendOverlapDmAsync(
        CalendarOutbox row,
        CalendarOutboxPayload payload,
        string googleEventId,
        bool isReschedule)
    {
        if (!payload.OrganizerId.HasValue) return;

        try
        {
            var overlaps = (await _calendarService.GetOverlappingEventsAsync(
                    payload.StartUtc, payload.EndUtc))
                .Where(e => e.Id != googleEventId)
                .ToList();

            if (overlaps.Count == 0) return;

            var guild = _client.GetGuild(row.GuildId);
            if (guild is null)
            {
                _logger.LogDebug(
                    "Overlap DM skipped: guild {GuildId} not in cache (bot restarted recently?)",
                    row.GuildId);
                return;
            }

            var organizer = guild.GetUser(payload.OrganizerId.Value);
            if (organizer is null)
            {
                _logger.LogDebug(
                    "Overlap DM skipped: organizer {OrganizerId} not in guild {Guild}",
                    payload.OrganizerId.Value, guild.Name);
                return;
            }

            var unixStart    = new DateTimeOffset(payload.StartUtc).ToUnixTimeSeconds();
            var overlapLines = overlaps.Select(e =>
            {
                var eStart  = GoogleCalendarService.ParseEventTime(e.Start);
                var timeStr = eStart.HasValue
                    ? $"<t:{new DateTimeOffset(eStart.Value).ToUnixTimeSeconds()}:F>"
                    : "unknown time";
                return $"• **{e.Summary}** — {timeStr}";
            });

            var action  = isReschedule ? "rescheduled" : "posted";
            var outcome = isReschedule
                ? "The calendar has been updated to the new time."
                : "The event has been added to the calendar.";

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Schedule Conflict Detected")
                .WithColor(Color.Orange)
                .WithDescription(
                    $"Hey {organizer.Mention}! The event you just {action} — " +
                    $"**{payload.Title}** (<t:{unixStart}:F>) — " +
                    $"overlaps with the following existing event(s):\n\n" +
                    string.Join("\n", overlapLines) +
                    $"\n\n{outcome} " +
                    "Please reach out to the other organizer(s) to coordinate or reschedule.")
                .WithFooter("ClanGuard Bot • Calendar Conflict Alert")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var dm = await organizer.CreateDMChannelAsync();
            await dm.SendMessageAsync(embed: embed);

            _logger.LogInformation(
                "Sent overlap DM to {Username} for event '{Title}' ({Count} conflicts)",
                organizer.Username, payload.Title, overlaps.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send overlap DM for CalendarOutbox row {RowId} ('{Title}')",
                row.Id, payload.Title);
        }
    }
}

/// <summary>
/// JSON envelope for CalendarOutbox.PayloadJson. Captured at enqueue time so a
/// later edit to the CalendarEvent doesn't retroactively change what an
/// in-flight outbox row will push to GCal.
/// </summary>
public class CalendarOutboxPayload
{
    public string Title { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Description { get; set; } = string.Empty;
    public string OrganizerName { get; set; } = string.Empty;
    public ulong? OrganizerId { get; set; }
    public string Source { get; set; } = "Clan";

    /// <summary>For Delete operations: the GCal event ID to remove. Required
    /// because CalendarEvent is deleted before the outbox row is processed.</summary>
    public string? GoogleEventId { get; set; }
}
