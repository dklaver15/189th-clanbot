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
/// Background service that snapshots meeting attendance shortly after each
/// clan event ends, mirroring EventAttendanceSnapshotService one-for-one.
/// Runs on the same timer cadence (default 5 min) plus a one-shot catch-up
/// pass at startup to cover events that ended while the bot was down.
///
/// ── Relationship to EventAttendanceSnapshotService ──
/// Both services process the same CalendarEvent rows along different axes:
///
///   Event-side:   any VC under EventsCategoryId, ≥30 min, writes EventAttendance,
///                 stamps CalendarEvent.LastSnapshotAttemptUtc.
///   Meeting-side: single VC at MeetingVoiceChannelId, ≥15 min, writes
///                 MeetingAttendance, stamps CalendarEvent.LastMeetingSnapshotAttemptUtc.
///
/// The two are independent — a single Apollo event can produce both kinds of
/// attendance rows for the same member if they happened to spend qualifying
/// time in both VCs (rare in practice). The shared CalendarEvent acts as the
/// pivot; both LastSnapshotAttemptUtc and LastMeetingSnapshotAttemptUtc exist
/// so each path's "given up after N min of zero qualifiers" check works
/// without affecting the other.
///
/// ── Why a single channel, not a category ──
/// Per clan doctrine: meetings are scoped to ONE specific voice channel
/// (BotConfig.MeetingVoiceChannelId). There is no "meetings category" with
/// breakout rooms — the room is the room. Sessions are matched by
/// ChannelId only; CategoryId is intentionally not consulted here. If a
/// legitimate case for breakout meetings ever arises this can be widened
/// to a MeetingsCategoryId fallback, mirroring the EventsVoiceChannelId →
/// EventsCategoryId widening done on the event side.
///
/// ── Which events count toward meeting attendance ──
/// Same source-list as the event side: AttendanceCountingSources (default
/// "Clan"). A meeting is still a clan-source Apollo event; only its VC
/// usage pattern differs. CompDiv events are excluded from both pipelines.
///
/// ── Opt-in ──
/// When BotConfig.MeetingVoiceChannelId == 0, the service logs a one-line
/// "feature disabled" notice and exits without consuming any DB or Discord
/// cycles. The whole meeting-attendance feature is opt-in by config; this
/// keeps the bot quiet in guilds that don't run meetings.
///
/// ── Idempotency, give-up policy, username capture, deleted events ──
/// All identical to EventAttendanceSnapshotService — see that file's
/// header for the full rationale. The retry window, attendance-row
/// existence check, fallback to UserActivity.Username for left-the-guild
/// users, and "30-second startup settle" are all preserved verbatim so the
/// two pipelines behave consistently.
/// </summary>
public class MeetingAttendanceSnapshotService : BackgroundService
{
    /// <summary>
    /// How long after EndUtc we keep retrying meeting snapshots when zero
    /// attendees have been written so far. Same generous 60-minute window
    /// as the event side — meetings can also have their EndUtc extended,
    /// and we don't want to give up while an operator is still pushing the
    /// window forward.
    /// </summary>
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(60);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<MeetingAttendanceSnapshotService> _logger;
    private readonly BotConfig _config;

    public MeetingAttendanceSnapshotService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<MeetingAttendanceSnapshotService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Feature gate: 0 means "no meeting VC configured" → quiet no-op.
        // We exit BEFORE waiting on Discord readiness so an unconfigured
        // deploy doesn't burn the startup-settle delay for nothing.
        if (_config.MeetingVoiceChannelId == 0)
        {
            _logger.LogInformation(
                "MeetingAttendanceSnapshotService disabled: MeetingVoiceChannelId is unset (0).");
            return;
        }

        // Wait for Discord client readiness. We don't strictly need the client
        // for the snapshot logic (everything is DB-only) but we gate on it so
        // we don't try to run during an unhealthy startup. Same pattern as
        // EventAttendanceSnapshotService.
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        // Same 30-second startup settle as the event side — lets voice-session
        // cleanup finish before we start querying.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var countingSources = _config.GetAttendanceCountingSourcesList();
        _logger.LogInformation(
            "MeetingAttendanceSnapshotService started. Interval={Interval}min, Buffer={Buffer}min, MinMinutes={Min}, RetryWindow={Retry}min, CountingSources=[{Sources}], MeetingChannelId={ChannelId}",
            _config.EventAttendanceSnapshotIntervalMinutes,
            _config.AutoPromotionEventBufferMinutes,
            _config.AutoPromotionMinMeetingAttendanceMinutes,
            (int)RetryWindow.TotalMinutes,
            string.Join(",", countingSources),
            _config.MeetingVoiceChannelId);

        // Startup catch-up — snapshot every past event that doesn't already
        // have a MeetingAttendance record for any attendees.
        try
        {
            await RunSweepAsync(isCatchUp: true, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during startup meeting-attendance catch-up");
        }

        // Steady-state loop. Reuses EventAttendanceSnapshotIntervalMinutes —
        // meetings and events share cadence by design.
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
                _logger.LogError(ex, "Error during meeting-attendance snapshot sweep");
            }
        }
    }

    /// <summary>
    /// Finds past CalendarEvents that don't yet have a meeting-attendance
    /// snapshot and computes one row per attending member for each. Mirrors
    /// EventAttendanceSnapshotService.RunSweepAsync — see that method for the
    /// full state-machine rationale. Differences here:
    ///   • "Already snapshotted" is determined from MeetingAttendances, not
    ///     EventAttendances.
    ///   • The give-up timestamp is CalendarEvent.LastMeetingSnapshotAttemptUtc.
    /// Everything else is identical.
    /// </summary>
    private async Task RunSweepAsync(bool isCatchUp, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var countingSources = _config.GetAttendanceCountingSourcesList()
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Left-anti-join shape: CalendarEvents that have no row in
        // MeetingAttendances yet. See the event-side sweep for the
        // performance discussion — same access pattern, same scaling.
        var snapshottedIds = await db.MeetingAttendances
            .Select(a => a.CalendarEventId)
            .Distinct()
            .ToListAsync(ct);

        var snapshottedSet = snapshottedIds.ToHashSet();

        var pastEvents = await db.CalendarEvents
            .Where(c => c.EndUtc <= now)
            .OrderBy(c => c.EndUtc)
            .ToListAsync(ct);

        var giveUpCutoff = now - RetryWindow;

        var processable = pastEvents.Where(e =>
        {
            if (snapshottedSet.Contains(e.Id)) return false;
            if (e.EndUtc > giveUpCutoff) return true;
            // Older than the retry window. Only process if we've never tried
            // the meeting-side snapshot for this event yet — same give-up
            // policy as the event side, on its own dedicated column so the
            // two paths' state never interferes.
            return e.LastMeetingSnapshotAttemptUtc is null;
        }).ToList();

        var counting = processable
            .Where(e => countingSources.Contains((e.Source ?? "").ToLowerInvariant()))
            .ToList();
        var skippedBySource = processable.Count - counting.Count;

        var givenUp = pastEvents.Count(e =>
            !snapshottedSet.Contains(e.Id)
            && e.EndUtc <= giveUpCutoff
            && e.LastMeetingSnapshotAttemptUtc is not null);

        if (counting.Count == 0)
        {
            if (isCatchUp)
            {
                _logger.LogInformation(
                    "Meeting-attendance catch-up: no counting events needing snapshots ({Skipped} non-counting, {GivenUp} given up after retry window)",
                    skippedBySource, givenUp);
            }
            else if (skippedBySource > 0 || givenUp > 0)
            {
                _logger.LogDebug(
                    "Meeting-attendance sweep: {Skipped} non-counting event(s) skipped, {GivenUp} given up, nothing else to process",
                    skippedBySource, givenUp);
            }
            return;
        }

        _logger.LogInformation(
            "Meeting-attendance sweep ({Mode}): processing {Counting} counting event(s), skipping {Skipped} non-counting, {GivenUp} given up",
            isCatchUp ? "catch-up" : "interval", counting.Count, skippedBySource, givenUp);

        var totalAttendances = 0;

        foreach (var evt in counting)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var rowsWritten = await SnapshotEventAsync(db, evt, ct);
                totalAttendances += rowsWritten;

                _logger.LogInformation(
                    "Meeting-attendance snapshot: event '{Title}' [{Source}] (Id={EventId}, ended {End:yyyy-MM-dd HH:mm} UTC) → {Attendees} attendee(s)",
                    evt.Title, evt.Source, evt.Id, evt.EndUtc, rowsWritten);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to snapshot meeting attendance for event '{Title}' (Id={EventId})",
                    evt.Title, evt.Id);
            }
        }

        _logger.LogInformation(
            "Meeting-attendance sweep complete: {Events} event(s) processed, {Rows} attendance row(s) written",
            counting.Count, totalAttendances);
    }

    /// <summary>
    /// Computes meeting attendance for a single event and writes one
    /// MeetingAttendance row per member who met the configured minimum-minutes
    /// threshold (default 15). Returns the number of rows written. Uses the
    /// unique index on (GuildId, UserId, CalendarEventId) on MeetingAttendances
    /// to stay idempotent across partial failures.
    ///
    /// Stamps CalendarEvent.LastMeetingSnapshotAttemptUtc regardless of outcome
    /// so the give-up rule in RunSweepAsync can identify events that have
    /// been tried and are stale on the meeting side specifically.
    ///
    /// ── Channel matching ──
    /// Sessions are matched by ChannelId == MeetingVoiceChannelId only. There
    /// is no category fallback and no legacy-row accommodation — the meeting
    /// feature was introduced after VoiceSession.CategoryId existed, so every
    /// session this service will ever see already has the columns it needs.
    /// </summary>
    private async Task<int> SnapshotEventAsync(BotDbContext db, CalendarEvent evt, CancellationToken ct)
    {
        var buffer           = TimeSpan.FromMinutes(_config.AutoPromotionEventBufferMinutes);
        var thresholdSeconds = (long)_config.AutoPromotionMinMeetingAttendanceMinutes * 60;
        var capSeconds       = _config.MaxSingleSessionHours > 0
            ? (long)(_config.MaxSingleSessionHours * 3600)
            : long.MaxValue;

        var winStart = evt.StartUtc - buffer;
        var winEnd   = evt.EndUtc   + buffer;

        var meetingChannelId = _config.MeetingVoiceChannelId;

        // Single-channel match. Compare on ChannelId only; no category
        // fallback (see method header).
        var sessions = await db.VoiceSessions
            .Where(v => v.GuildId == evt.GuildId
                     && v.ChannelId == meetingChannelId
                     && v.JoinedAt <  winEnd
                     && (v.LeftAt  == null || v.LeftAt > winStart))
            .ToListAsync(ct);

        // userId → total seconds attended (in the meeting VC during the
        // buffered window, with per-session cap applied).
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

        // Filter to users meeting the threshold.
        var qualifyingUsers = perUser
            .Where(kv => kv.Value >= thresholdSeconds)
            .ToList();

        // Resolve a display name for each qualifier. Same primary →
        // fallback chain as EventAttendanceSnapshotService: live
        // SocketGuildUser.DisplayName preferred, UserActivity.Username as
        // the durable fallback for members who left between meeting end
        // and snapshot run.
        var guild = _client.GetGuild(evt.GuildId);

        var unresolvedIds = qualifyingUsers
            .Where(kv => guild?.GetUser(kv.Key) is null)
            .Select(kv => kv.Key)
            .ToList();

        var fallbackUsernames = new Dictionary<ulong, string>();
        if (unresolvedIds.Count > 0)
        {
            fallbackUsernames = await db.UserActivities
                .Where(ua => ua.GuildId == evt.GuildId && unresolvedIds.Contains(ua.UserId))
                .ToDictionaryAsync(ua => ua.UserId, ua => ua.Username, ct);
        }

        var now = DateTime.UtcNow;
        var toInsert = qualifyingUsers
            .Select(kv =>
            {
                var member = guild?.GetUser(kv.Key);
                var displayName = member?.DisplayName
                    ?? (fallbackUsernames.TryGetValue(kv.Key, out var fb) ? fb : string.Empty);

                return new MeetingAttendance
                {
                    GuildId         = evt.GuildId,
                    UserId          = kv.Key,
                    CalendarEventId = evt.Id,
                    Username        = displayName,
                    EventStartUtc   = evt.StartUtc,
                    EventEndUtc     = evt.EndUtc,
                    AttendedMinutes = (int)(kv.Value / 60),
                    RecordedAt      = now
                };
            })
            .ToList();

        // Always stamp the attempt timestamp, even for zero-attendee runs —
        // that's the give-up signal. Atomically with the inserts so partial
        // failures can't leave the row half-stamped. Same pattern as the
        // event-side service.
        evt.LastMeetingSnapshotAttemptUtc = now;
        if (toInsert.Count > 0)
            db.MeetingAttendances.AddRange(toInsert);
        await db.SaveChangesAsync(ct);

        return toInsert.Count;
    }
}
