using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// The scheduling "brain" for meeting recordings. Pure orchestration — it
/// never touches voice itself; that's delegated to <see cref="IMeetingRecorderController"/>.
///
/// On a fixed cadence it:
///   1. Finds the next upcoming meeting by matching CalendarEvent.Title against
///      MeetingTitlePattern (a regex), restricted to the attendance-counting
///      sources ("Clan"). No day/time is ever hardcoded.
///   2. Upserts a MeetingRecording row for it, recomputing JoinAtUtc when the
///      event is rescheduled and refreshing the title on rename (pre-recording
///      states only).
///   3. Reconciles cancellations — if a scheduled meeting's CalendarEvent has
///      disappeared (Apollo event deleted), the recording is Cancelled.
///   4. Fires the recorder at JoinAtUtc: posts the recording-consent notice,
///      then asks the controller to start.
///
/// ── Stop policy (changed) ──────────────────────────────────────────────────
/// The recorder now decides when a meeting actually ends — it stops once the VC
/// has been empty for a grace period (so meetings that run long are captured in
/// full), bounded by a hard safety cap. The scheduler therefore does NOT stop
/// the recorder at a fixed calendar buffer anymore. Instead, once Recording, it
/// asks the recorder for the finalized audio dir only after a generous backstop
/// window (EndUtc + StopBackstop) has elapsed — by which point the recorder has
/// almost always already auto-stopped, and StopRecordingAsync simply returns the
/// dir it finalized (idempotent). The bot's stop is a safety net, not the
/// primary trigger.
///
/// ── Never lose captured audio (changed) ────────────────────────────────────
/// If StopRecordingAsync returns null (e.g. the recorder restarted and lost its
/// in-memory state), the scheduler reconciles against the shared audio volume:
/// it looks for a meeting_{id}_* directory containing a manifest.json with real
/// tracks/segments and adopts it. A recording is only marked Failed when there
/// is genuinely no audio on disk. This is the fix for the May-31 stop-race that
/// wrongly failed a meeting whose 2,518 segments were sitting on disk.
///
/// Restart-safe: all state lives in the MeetingRecordings table.
///
/// Feature-gated: a no-op unless MeetingRecordingEnabled is true AND both
/// MeetingVoiceChannelId and MeetingTitlePattern are configured.
/// </summary>
public class MeetingRecordingScheduler : BackgroundService
{
    /// <summary>
    /// How long past the scheduled EndUtc the scheduler waits before issuing its
    /// backstop stop. Large on purpose: the recorder owns the real (VC-empty)
    /// stop, so this only fires if the recorder never reported in. Must be
    /// comfortably larger than the recorder's own VC-empty grace.
    /// </summary>
    private static readonly TimeSpan StopBackstop = TimeSpan.FromHours(4);

    /// <summary>
    /// The "expected stop" we hand the recorder for its hard-cap math. The
    /// recorder adds its own grace on top; this is just a hint, not a deadline.
    /// </summary>
    private static readonly TimeSpan ExpectedStopBuffer = TimeSpan.FromMinutes(15);

    /// <summary>Don't schedule meetings further out than this.</summary>
    private static readonly TimeSpan SchedulingHorizon = TimeSpan.FromDays(60);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly IMeetingRecorderController _recorder;
    private readonly ILogger<MeetingRecordingScheduler> _logger;
    private readonly BotConfig _config;

    private Regex? _titleRegex;

    // Remembers the recording-notice message posted per recording, so it can be
    // deleted when recording stops. In-memory by design (cosmetic edge case).
    private readonly ConcurrentDictionary<int, (ulong ChannelId, ulong MessageId)> _noticeMessages = new();

    public MeetingRecordingScheduler(
        IServiceProvider services,
        DiscordSocketClient client,
        IMeetingRecorderController recorder,
        ILogger<MeetingRecordingScheduler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _recorder = recorder;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.MeetingRecordingEnabled)
        {
            _logger.LogInformation("MeetingRecordingScheduler disabled: MeetingRecordingEnabled is false.");
            return;
        }

        if (_config.MeetingVoiceChannelId == 0 || string.IsNullOrWhiteSpace(_config.MeetingTitlePattern))
        {
            _logger.LogWarning(
                "MeetingRecordingScheduler disabled: requires MeetingVoiceChannelId (got {ChannelId}) " +
                "and MeetingTitlePattern (got '{Pattern}').",
                _config.MeetingVoiceChannelId, _config.MeetingTitlePattern);
            return;
        }

        try
        {
            _titleRegex = new Regex(_config.MeetingTitlePattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex,
                "MeetingRecordingScheduler disabled: MeetingTitlePattern '{Pattern}' is not a valid regex.",
                _config.MeetingTitlePattern);
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.MeetingRecordingPollIntervalMinutes));
        _logger.LogInformation(
            "MeetingRecordingScheduler started. Pattern='{Pattern}', VC={ChannelId}, Lead={Lead}min, " +
            "Poll={Poll}min, Retain={Retain}.",
            _config.MeetingTitlePattern, _config.MeetingVoiceChannelId, _config.MeetingRecordingLeadMinutes,
            _config.MeetingRecordingPollIntervalMinutes, _config.MeetingRecordingRetainCount);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MeetingRecordingScheduler poll failed; will retry next interval.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var now = DateTime.UtcNow;

        await DiscoverAndUpsertAsync(db, now, ct);
        await ReconcileCancellationsAsync(db, now, ct);
        await DriveStateMachineAsync(db, now, ct);

        await db.SaveChangesAsync(ct);
    }

    // ── 1. Find the next matching meeting and create/refresh its row ──────────
    private async Task DiscoverAndUpsertAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        var sources = _config.GetAttendanceCountingSourcesList();
        var leadGrace = TimeSpan.FromMinutes(_config.MeetingRecordingLeadMinutes + 5);

        var candidates = await db.CalendarEvents
            .Where(e => e.StartUtc > now - leadGrace
                     && e.StartUtc < now + SchedulingHorizon
                     && sources.Contains(e.Source))
            .OrderBy(e => e.StartUtc)
            .ToListAsync(ct);

        var match = candidates.FirstOrDefault(e =>
            !string.IsNullOrEmpty(e.Title) && _titleRegex!.IsMatch(e.Title));

        if (match is null)
            return;

        var joinAt = match.StartUtc.AddMinutes(-_config.MeetingRecordingLeadMinutes);

        // Match the recording for this occurrence in ANY state, including terminal
        // ones. A single meeting (DiscordMessageId) must never spawn a second row:
        // its CalendarEvent stays a discovery candidate for a short window after
        // StartUtc, so a meeting that already ran and reached a terminal state
        // (Posted/Pruned/Failed/Cancelled) would otherwise be re-created here and
        // re-recorded. Reschedule/rename below is still gated to pre-recording
        // states, so finding a terminal row simply means "leave it alone."
        var existing = await db.MeetingRecordings
            .FirstOrDefaultAsync(m => m.DiscordMessageId == match.DiscordMessageId, ct);

        if (existing is null)
        {
            db.MeetingRecordings.Add(new MeetingRecording
            {
                GuildId          = match.GuildId,
                DiscordMessageId = match.DiscordMessageId,
                CalendarEventId  = match.Id,
                MeetingTitle     = match.Title,
                MeetingStartUtc  = match.StartUtc,
                MeetingEndUtc    = match.EndUtc,
                JoinAtUtc        = joinAt,
                State            = MeetingRecordingState.Scheduled,
                StateUpdatedUtc  = now,
                CreatedAt        = now,
            });
            _logger.LogInformation(
                "Scheduled recording for '{Title}' starting {Start:yyyy-MM-dd HH:mm} UTC (join at {Join:HH:mm}).",
                match.Title, match.StartUtc, joinAt);
            return;
        }

        if (existing.State is MeetingRecordingState.Scheduled or MeetingRecordingState.Announced)
        {
            var moved   = existing.MeetingStartUtc != match.StartUtc || existing.MeetingEndUtc != match.EndUtc;
            var renamed = existing.MeetingTitle != match.Title;

            if (moved || renamed)
            {
                if (moved)
                    _logger.LogInformation(
                        "Meeting '{Title}' rescheduled {Old:yyyy-MM-dd HH:mm} → {New:yyyy-MM-dd HH:mm} UTC; " +
                        "recomputed join to {Join:HH:mm}.",
                        match.Title, existing.MeetingStartUtc, match.StartUtc, joinAt);
                if (renamed)
                    _logger.LogInformation("Meeting renamed '{Old}' → '{New}'.", existing.MeetingTitle, match.Title);

                existing.MeetingTitle    = match.Title;
                existing.MeetingStartUtc = match.StartUtc;
                existing.MeetingEndUtc   = match.EndUtc;
                existing.JoinAtUtc       = joinAt;
                existing.CalendarEventId = match.Id;

                if (existing.State == MeetingRecordingState.Announced && joinAt > now)
                    Transition(existing, MeetingRecordingState.Scheduled, now);
            }
        }
    }

    // ── 2. Cancel recordings whose meeting vanished (Apollo event deleted) ────
    private async Task ReconcileCancellationsAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        var pending = await db.MeetingRecordings
            .Where(m => m.State == MeetingRecordingState.Scheduled
                     || m.State == MeetingRecordingState.Announced)
            .ToListAsync(ct);

        foreach (var rec in pending)
        {
            var stillExists = await db.CalendarEvents
                .AnyAsync(e => e.DiscordMessageId == rec.DiscordMessageId, ct);

            if (!stillExists)
            {
                _logger.LogInformation(
                    "Meeting '{Title}' (msg {Msg}) was cancelled before recording — marking Cancelled.",
                    rec.MeetingTitle, rec.DiscordMessageId);
                await DeleteAnnouncementAsync(rec, ct);
                Transition(rec, MeetingRecordingState.Cancelled, now);
            }
        }
    }

    // ── 3. Announce → start → (recorder auto-stops) → backstop stop → hand off ─
    private async Task DriveStateMachineAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        var active = await db.MeetingRecordings
            .Where(m => m.State == MeetingRecordingState.Scheduled
                     || m.State == MeetingRecordingState.Announced
                     || m.State == MeetingRecordingState.Recording)
            .ToListAsync(ct);

        foreach (var rec in active)
        {
            // The recorder owns the real stop; the bot's backstop is far out.
            var backstopAt = rec.MeetingEndUtc + StopBackstop;
            // The window in which we still bother trying to START if we haven't.
            var startWindowEnd = rec.MeetingEndUtc + TimeSpan.FromMinutes(_config.MeetingRecordingLeadMinutes + 30);

            // Missed the whole start window without ever starting — give up.
            if (rec.State is MeetingRecordingState.Scheduled or MeetingRecordingState.Announced
                && now >= startWindowEnd)
            {
                rec.ErrorMessage = "Recording window elapsed without a successful start.";
                _logger.LogWarning("Recording for '{Title}' (#{Id}) missed its window — Failed.",
                    rec.MeetingTitle, rec.Id);
                await DeleteAnnouncementAsync(rec, ct);
                Transition(rec, MeetingRecordingState.Failed, now);
                continue;
            }

            // Announce once, at lead time.
            if (rec.State == MeetingRecordingState.Scheduled && now >= rec.JoinAtUtc && now < startWindowEnd)
            {
                await PostAnnouncementAsync(rec, ct);
                Transition(rec, MeetingRecordingState.Announced, now);
            }

            // Start (retried each poll while Announced and still within the window).
            if (rec.State == MeetingRecordingState.Announced && now < startWindowEnd)
            {
                try
                {
                    // ExpectedStop is just a hint for the recorder's hard-cap math;
                    // the real stop is VC-empty-driven inside the recorder.
                    var expectedStop = rec.MeetingEndUtc + ExpectedStopBuffer;
                    await _recorder.StartRecordingAsync(new MeetingRecorderStartContext(
                        rec.Id, rec.GuildId, _config.MeetingVoiceChannelId, rec.MeetingTitle, expectedStop), ct);
                    rec.RecordingStartedUtc = now;
                    rec.ErrorMessage = null;
                    Transition(rec, MeetingRecordingState.Recording, now);
                }
                catch (Exception ex)
                {
                    rec.ErrorMessage = Truncate(ex.Message, 800);
                    _logger.LogError(ex,
                        "Failed to start recorder for '{Title}' (#{Id}); will retry until {End:HH:mm} UTC.",
                        rec.MeetingTitle, rec.Id, startWindowEnd);
                }
                continue;
            }

            // While Recording: the recorder auto-stops when the VC empties and
            // remembers the finalized result. As soon as it reports this meeting as
            // finalized, advance promptly (StopRecordingAsync then just returns the
            // already-finalized dir). The backstop far past EndUtc is the fallback
            // for when the recorder can't be reached or restarted and lost state.
            if (rec.State == MeetingRecordingState.Recording)
            {
                if (await _recorder.IsFinalizedAsync(rec.Id, ct))
                    await FinalizeRecordingAsync(rec, now, ct, reason: "recorder-finalized");
                else if (now >= backstopAt)
                    await FinalizeRecordingAsync(rec, now, ct, reason: "backstop");
            }
        }
    }

    /// <summary>
    /// Ask the recorder for the finalized audio dir and advance to Transcribing.
    /// If the recorder returns nothing, reconcile against the shared volume so a
    /// recording with audio on disk is NEVER wrongly failed. Only when no audio
    /// exists anywhere is the row failed.
    /// </summary>
    private async Task FinalizeRecordingAsync(MeetingRecording rec, DateTime now, CancellationToken ct, string reason)
    {
        string? dir = null;
        try
        {
            dir = await _recorder.StopRecordingAsync(rec.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Recorder StopRecordingAsync failed for '{Title}' (#{Id}); will try disk reconciliation.",
                rec.MeetingTitle, rec.Id);
        }

        // Fallback: find a finalized manifest on the shared volume for this id.
        dir ??= TryReconcileFromDisk(rec.Id);

        if (string.IsNullOrWhiteSpace(dir))
        {
            // Genuinely nothing captured — fail (the only legitimate Failed here).
            rec.ErrorMessage = "No audio was captured for this meeting.";
            _logger.LogWarning("Recording for '{Title}' (#{Id}) produced no audio ({Reason}) — Failed.",
                rec.MeetingTitle, rec.Id, reason);
            await DeleteAnnouncementAsync(rec, ct);
            Transition(rec, MeetingRecordingState.Failed, now);
            return;
        }

        rec.RecordingStoppedUtc = now;
        rec.AudioDirPath = dir;
        rec.ErrorMessage = null;
        await DeleteAnnouncementAsync(rec, ct);
        Transition(rec, MeetingRecordingState.Transcribing, now);
        _logger.LogInformation("Stopped recording '{Title}' (#{Id}, {Reason}); audio dir: {Dir}.",
            rec.MeetingTitle, rec.Id, reason, dir);
    }

    /// <summary>
    /// Look on the shared audio volume for a finalized recording directory for
    /// this meeting id (meeting_{id}_*) whose manifest.json reports real audio.
    /// Returns the directory path, or null if none. This makes the pipeline
    /// robust to the recorder losing its in-memory state (restart) after it had
    /// already written the audio + manifest — the May-31 failure mode.
    /// </summary>
    private string? TryReconcileFromDisk(int meetingRecordingId)
    {
        var root = _config.MeetingRecordingsPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;

        try
        {
            var prefix = $"meeting_{meetingRecordingId}_";
            // Newest first, in case of a retry that produced more than one dir.
            var candidates = Directory.EnumerateDirectories(root, prefix + "*")
                .OrderByDescending(d => d)
                .ToList();

            foreach (var dir in candidates)
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    var rootEl = doc.RootElement;

                    var hasTracks = rootEl.TryGetProperty("tracks", out var tracks)
                                    && tracks.ValueKind == JsonValueKind.Array
                                    && tracks.GetArrayLength() > 0;
                    var hasSegments = rootEl.TryGetProperty("segments", out var segs)
                                      && segs.ValueKind == JsonValueKind.Array
                                      && segs.GetArrayLength() > 0;

                    if (hasTracks || hasSegments)
                    {
                        _logger.LogInformation(
                            "Reconciled recording #{Id} from disk: {Dir} (tracks={Tracks}, segments={Segs}).",
                            meetingRecordingId, dir,
                            hasTracks ? tracks.GetArrayLength() : 0,
                            hasSegments ? segs.GetArrayLength() : 0);
                        return dir;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not parse manifest at {Path}; skipping.", manifestPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disk reconciliation for #{Id} failed.", meetingRecordingId);
        }

        return null;
    }

    private async Task PostAnnouncementAsync(MeetingRecording rec, CancellationToken ct)
    {
        var channelId = _config.MeetingRecordingAnnouncementChannelId != 0
            ? _config.MeetingRecordingAnnouncementChannelId
            : _config.MeetingVoiceChannelId;

        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            _logger.LogWarning(
                "Could not resolve announcement channel {ChannelId} — recording will proceed unannounced.",
                channelId);
            return;
        }

        var notice =
            $"🔴 **Recording notice** — **{rec.MeetingTitle}** is being recorded by ClanGuard to generate " +
            $"minutes and action items. Only the last {_config.MeetingRecordingRetainCount} meetings' audio is " +
            "kept; older recordings are deleted automatically. If you'd rather not be recorded, please leave " +
            "the voice channel — staying in the channel indicates your consent to being recorded.";

        try
        {
            var sent = await channel.SendMessageAsync(notice, options: new RequestOptions { CancelToken = ct });
            _noticeMessages[rec.Id] = (channelId, sent.Id);
            _logger.LogInformation("Posted recording notice for '{Title}' to channel {ChannelId}.",
                rec.MeetingTitle, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to post recording notice for '{Title}' to channel {ChannelId} — recording will proceed unannounced.",
                rec.MeetingTitle, channelId);
        }
    }

    private async Task DeleteAnnouncementAsync(MeetingRecording rec, CancellationToken ct)
    {
        if (!_noticeMessages.TryRemove(rec.Id, out var notice)) return;
        try
        {
            if (_client.GetChannel(notice.ChannelId) is IMessageChannel channel)
            {
                await channel.DeleteMessageAsync(notice.MessageId, new RequestOptions { CancelToken = ct });
                _logger.LogDebug("Deleted recording notice for '{Title}' (#{Id}).", rec.MeetingTitle, rec.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not delete recording notice for '{Title}' (#{Id}) — leaving it in place.",
                rec.MeetingTitle, rec.Id);
        }
    }

    private void Transition(MeetingRecording rec, MeetingRecordingState to, DateTime now)
    {
        if (rec.State == to) return;
        _logger.LogDebug("Recording #{Id} '{Title}': {From} → {To}.", rec.Id, rec.MeetingTitle, rec.State, to);
        rec.State = to;
        rec.StateUpdatedUtc = now;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
