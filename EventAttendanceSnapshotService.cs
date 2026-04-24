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
/// Background service that snapshots event attendance shortly after each clan
/// event ends. Runs on a timer (default every 5 minutes) plus a one-shot
/// catch-up pass at startup to cover events that ended while the bot was down.
///
/// ── Why this exists ──
/// CalendarEvent rows can be deleted when their originating Apollo Discord
/// message is removed from #events (either manually or by Apollo's own auto-
/// cleanup). That makes CalendarEvents unsuitable as the long-term source of
/// truth for attendance. This service computes attendance per user per event
/// shortly after the event ends and writes durable EventAttendance rows with
/// denormalized event times, so the historical record survives CalendarEvent
/// deletion.
///
/// ── What counts as "attended" ──
/// A member attended an event if their cumulative time in ANY voice channel
/// under the EVENTS category (matched by EventsCategoryId) during
/// [event.StartUtc - bufferMinutes, event.EndUtc + bufferMinutes] meets or
/// exceeds AutoPromotionMinEventAttendanceMinutes. Temp event VCs created
/// under that category — e.g. when a squad splits off from the main Events VC
/// mid-event — count just like the main Events VC. Per-session duration is
/// capped by MaxSingleSessionHours to prevent orphaned sessions from granting
/// unlimited credit.
///
/// Sessions recorded before the CategoryId column existed (null CategoryId)
/// fall back to matching on EventsVoiceChannelId so historical snapshots
/// keep producing the same results they always did.
///
/// ── Which events count toward promotion ──
/// Only events whose Source is in AttendanceCountingSources (default: "Clan")
/// are processed. CompDiv events — internal comp-team scrims — don't count
/// toward clan-wide promotion points and are skipped, even though they're
/// visible in the calendar and #events channel.
///
/// ── Idempotency ──
/// The unique index on (GuildId, UserId, CalendarEventId) means we can
/// re-snapshot the same event safely. We skip events that already have at least
/// one EventAttendance row, so repeated sweeps are essentially free.
///
/// ── Deleted events ──
/// If a CalendarEvent gets deleted between event end and the next sweep, that
/// event's attendance is lost (we can't snapshot what isn't there). The 5-min
/// polling interval plus the startup catch-up pass minimize this window to a
/// few minutes at worst.
/// </summary>
public class EventAttendanceSnapshotService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<EventAttendanceSnapshotService> _logger;
    private readonly BotConfig _config;

    public EventAttendanceSnapshotService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<EventAttendanceSnapshotService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord client readiness. We don't strictly need the client
        // for the snapshot logic (everything is DB-only) but we gate on it so
        // we don't try to run during an unhealthy startup.
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        // Small buffer so other startup services (Apollo backfill, voice cleanup)
        // settle first. The Apollo backfill in particular may be inserting
        // CalendarEvent rows we'd want to snapshot.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var countingSources = _config.GetAttendanceCountingSourcesList();
        _logger.LogInformation(
            "EventAttendanceSnapshotService started. Interval={Interval}min, Buffer={Buffer}min, MinMinutes={Min}, CountingSources=[{Sources}], EventsCategoryId={CategoryId}",
            _config.EventAttendanceSnapshotIntervalMinutes,
            _config.AutoPromotionEventBufferMinutes,
            _config.AutoPromotionMinEventAttendanceMinutes,
            string.Join(",", countingSources),
            _config.EventsCategoryId);

        // Startup catch-up — snapshot every past event that doesn't already
        // have attendance records. This handles two cases:
        //   1. Events that ended while the bot was down
        //   2. Backfilling existing past events on first deploy of this feature
        try
        {
            await RunSweepAsync(isCatchUp: true, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup attendance catch-up");
        }

        // Steady-state loop
        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.EventAttendanceSnapshotIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                await RunSweepAsync(isCatchUp: false, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during attendance snapshot sweep");
            }
        }
    }

    /// <summary>
    /// Finds past CalendarEvents that don't yet have attendance snapshots and
    /// computes one row per attending member for each. Events whose Source is
    /// not in the configured counting list (e.g. CompDiv) are skipped entirely
    /// — they don't count toward promotion points and thus don't need snapshots.
    ///
    /// Idempotent — safe to call repeatedly; events that already have any
    /// EventAttendance row are skipped.
    /// </summary>
    private async Task RunSweepAsync(bool isCatchUp, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var countingSources = _config.GetAttendanceCountingSourcesList()
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Find past events that haven't been snapshotted yet. "Haven't been
        // snapshotted" = no EventAttendance row references this CalendarEvent.Id.
        //
        // We use a left-anti-join pattern. For small event volumes (a few per
        // week) this is trivially cheap; if event volume ever grows to hundreds
        // per day, switch to a flag column on CalendarEvent.
        var snapshottedIds = await db.EventAttendances
            .Select(a => a.CalendarEventId)
            .Distinct()
            .ToListAsync(ct);

        var snapshottedSet = snapshottedIds.ToHashSet();

        var pastEvents = await db.CalendarEvents
            .Where(c => c.EndUtc <= now)
            .OrderBy(c => c.EndUtc)
            .ToListAsync(ct);

        var unsnapshotted = pastEvents
            .Where(e => !snapshottedSet.Contains(e.Id))
            .ToList();

        // Split unsnapshotted events by whether they count toward attendance.
        // Non-counting events (CompDiv, etc.) are not worth snapshotting — they'll
        // just produce empty results — but we log them so you can see they were
        // considered and skipped.
        var counting = unsnapshotted
            .Where(e => countingSources.Contains((e.Source ?? "").ToLowerInvariant()))
            .ToList();
        var skippedBySource = unsnapshotted.Count - counting.Count;

        if (counting.Count == 0)
        {
            if (isCatchUp)
            {
                _logger.LogInformation(
                    "Attendance catch-up: no counting events needing snapshots ({Skipped} non-counting event(s) skipped)",
                    skippedBySource);
            }
            else if (skippedBySource > 0)
            {
                _logger.LogDebug(
                    "Attendance sweep: {Skipped} non-counting event(s) skipped, nothing else to process",
                    skippedBySource);
            }
            return;
        }

        _logger.LogInformation(
            "Attendance sweep ({Mode}): processing {Counting} counting event(s), skipping {Skipped} non-counting",
            isCatchUp ? "catch-up" : "interval", counting.Count, skippedBySource);

        var totalAttendances = 0;

        foreach (var evt in counting)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var rowsWritten = await SnapshotEventAsync(db, evt, ct);
                totalAttendances += rowsWritten;

                _logger.LogInformation(
                    "Attendance snapshot: event '{Title}' [{Source}] (Id={EventId}, ended {End:yyyy-MM-dd HH:mm} UTC) → {Attendees} attendee(s)",
                    evt.Title, evt.Source, evt.Id, evt.EndUtc, rowsWritten);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to snapshot attendance for event '{Title}' (Id={EventId})",
                    evt.Title, evt.Id);
            }
        }

        _logger.LogInformation(
            "Attendance sweep complete: {Events} event(s) processed, {Rows} attendance row(s) written",
            counting.Count, totalAttendances);
    }

    /// <summary>
    /// Computes attendance for a single event and writes one EventAttendance row
    /// per member who met the minimum-minutes threshold. Returns the number of
    /// rows written. Uses the unique index on (GuildId, UserId, CalendarEventId)
    /// to stay idempotent across partial failures.
    /// </summary>
    private async Task<int> SnapshotEventAsync(BotDbContext db, CalendarEvent evt, CancellationToken ct)
    {
        var buffer           = TimeSpan.FromMinutes(_config.AutoPromotionEventBufferMinutes);
        var thresholdSeconds = (long)_config.AutoPromotionMinEventAttendanceMinutes * 60;
        var capSeconds       = _config.MaxSingleSessionHours > 0
            ? (long)(_config.MaxSingleSessionHours * 3600)
            : long.MaxValue;

        var winStart = evt.StartUtc - buffer;
        var winEnd   = evt.EndUtc   + buffer;

        // Pull every session that overlaps the buffered window AND is in the
        // EVENTS category (or — for pre-migration rows where CategoryId is
        // null — matches the legacy EventsVoiceChannelId). This means time in
        // temp event VCs created under the EVENTS category counts just like
        // time in the main Events VC, while historical data behaves exactly
        // as it did before the column existed.
        //
        // Group-by happens client-side after clipping, since we need to apply
        // the per-session cap to each session's clipped duration individually.
        var categoryId = _config.EventsCategoryId;
        var legacyChannelId = _config.EventsVoiceChannelId;

        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == evt.GuildId
                     && (v.CategoryId == categoryId
                         || (v.CategoryId == null && v.ChannelId == legacyChannelId))
                     && v.JoinedAt <  winEnd
                     && (v.LeftAt  == null || v.LeftAt > winStart))
            .ToListAsync(ct);

        // userId → total seconds attended
        var perUser = new Dictionary<ulong, long>();

        foreach (var s in sessions)
        {
            var sessionEnd = s.LeftAt ?? DateTime.UtcNow;

            var overlapStart = s.JoinedAt > winStart ? s.JoinedAt  : winStart;
            var overlapEnd   = sessionEnd < winEnd   ? sessionEnd  : winEnd;
            var overlapSec   = (long)(overlapEnd - overlapStart).TotalSeconds;

            if (overlapSec <= 0) continue;

            var credited = Math.Min(overlapSec, capSeconds);
            perUser[s.UserId] = perUser.TryGetValue(s.UserId, out var existing)
                ? existing + credited
                : credited;
        }

        // Filter to users meeting the threshold and write rows.
        var now = DateTime.UtcNow;
        var toInsert = perUser
            .Where(kv => kv.Value >= thresholdSeconds)
            .Select(kv => new EventAttendance
            {
                GuildId         = evt.GuildId,
                UserId          = kv.Key,
                CalendarEventId = evt.Id,
                EventStartUtc   = evt.StartUtc,
                EventEndUtc     = evt.EndUtc,
                AttendedMinutes = (int)(kv.Value / 60),
                RecordedAt      = now
            })
            .ToList();

        if (toInsert.Count == 0) return 0;

        db.EventAttendances.AddRange(toInsert);
        await db.SaveChangesAsync(ct);
        return toInsert.Count;
    }
}