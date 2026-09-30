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
/// The scheduling "brain" for meeting recordings. Pure orchestration — it never
/// touches voice itself; that's delegated to <see cref="IMeetingRecorderController"/>.
///
/// ── Trigger: presence, not calendar (changed) ──────────────────────────────
/// Recording is driven entirely by who is *in the meeting voice channel*, not by
/// any calendar event or title. On each poll (and immediately when someone joins
/// the VC) the scheduler checks the live occupancy of MeetingVoiceChannelId:
///   • Once at least MeetingRecordingMinPresenceToStart non-bot members are
///     present and nothing is already recording, it posts the consent notice,
///     asks the recorder to join, and moves the row to Recording. No event needs
///     to exist and the meeting can be titled anything.
///   • A recording is *named* by borrowing the title of a clan-source Apollo
///     event that happens to overlap the moment it starts (preferring one whose
///     title matches the optional MeetingTitlePattern); if there is no concurrent
///     event, it's named after the voice channel and the date.
///
/// ── Stop policy ────────────────────────────────────────────────────────────
/// The recorder owns the real stop — it auto-stops once the VC has been empty for
/// a grace period (so long meetings are captured in full), bounded by a hard cap.
/// The scheduler advances Recording → Transcribing as soon as the recorder reports
/// the meeting finalized, or as soon as the recorder reports it is no longer
/// recording that meeting (it restarted, e.g. during a deploy). A backstop
/// (recording start + StopBackstop) covers a recorder that stays unreachable.
///
/// ── Resuming a meeting that was cut off ────────────────────────────────────
/// When a recording ends because the recorder disconnected, shut down, or lost
/// it, the meeting is usually still going. The scheduler then skips its start
/// cooldown for ResumeWindow and starts a new recording titled "… (continued)"
/// if people are still in the VC, so only the restart gap is lost.
///
/// ── Never lose captured audio ──────────────────────────────────────────────
/// If StopRecordingAsync returns null (e.g. the recorder restarted and lost its
/// in-memory state), the scheduler reconciles against the shared audio volume: it
/// looks for a meeting_{id}_* directory whose manifest.json has real
/// tracks/segments and adopts it. If the recorder was killed before writing
/// manifest.json, its periodic manifest.partial.json checkpoint is promoted
/// instead. A recording is only marked Failed when there is genuinely no audio
/// on disk.
///
/// Restart-safe: all state lives in the MeetingRecordings table. On restart, a
/// meeting still in progress stays in its Recording row (the presence guard stops
/// a duplicate from being created) and the recorder/backstop finalize it.
///
/// Feature-gated: a no-op unless MeetingRecordingEnabled is true AND
/// MeetingVoiceChannelId is configured.
/// </summary>
public class MeetingRecordingScheduler : BackgroundService
{
    /// <summary>
    /// The longest a meeting may record. Sent to the recorder as its hard cap.
    /// </summary>
    private static readonly TimeSpan ExpectedMeetingDuration = TimeSpan.FromHours(3);

    /// <summary>
    /// How long past the recording start the scheduler waits before issuing its
    /// backstop stop. Slightly past the recorder's own hard cap, so the recorder
    /// normally finalizes first and this only fires if it can't be reached.
    /// </summary>
    private static readonly TimeSpan StopBackstop = ExpectedMeetingDuration + TimeSpan.FromMinutes(15);

    /// <summary>
    /// After a recording is cut off (recorder disconnected, shut down, or lost
    /// it), how long the scheduler will start a continuation recording without
    /// waiting out <see cref="StartCooldown"/>.
    /// </summary>
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(15);

    /// <summary>Recorder stop reasons that mean the meeting was cut off, not over.</summary>
    private static readonly HashSet<string> CutOffReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "disconnected", "shutdown",
    };

    /// <summary>
    /// How long a row may sit Announced (notice posted, trying to start) before we
    /// give up and fail it. Covers a recorder that's unreachable at start time.
    /// </summary>
    private static readonly TimeSpan StartWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// After a recording stops, ignore presence in the same channel for this long
    /// so a meeting that briefly empties and refills isn't split into a second
    /// recording the instant the first one finalizes.
    /// </summary>
    private static readonly TimeSpan StartCooldown = TimeSpan.FromMinutes(5);

    private const string ContinuedSuffix = " (continued)";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly IMeetingRecorderController _recorder;
    private readonly ILogger<MeetingRecordingScheduler> _logger;
    private readonly BotConfig _config;

    // Optional — only used to prefer a concurrent event when naming a recording.
    private Regex? _titleRegex;

    // Wakes the poll loop early when someone joins the meeting VC, so recording
    // starts within moments of a conversation forming rather than at the next poll.
    private volatile TaskCompletionSource _wake =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Remembers the recording-notice message posted per recording, so it can be
    // deleted when recording stops. In-memory by design (cosmetic edge case).
    private readonly ConcurrentDictionary<int, (ulong ChannelId, ulong MessageId)> _noticeMessages = new();

    // Set when a recording is cut off mid-meeting; lets the next start skip the
    // cooldown and carry the title forward. Only touched from the poll loop.
    private (string Title, DateTime UntilUtc)? _resume;

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

        if (_config.MeetingVoiceChannelId == 0)
        {
            _logger.LogWarning(
                "MeetingRecordingScheduler disabled: requires MeetingVoiceChannelId (got {ChannelId}).",
                _config.MeetingVoiceChannelId);
            return;
        }

        // The title pattern is optional now — used only to prefer a concurrent
        // event when naming a recording. A bad pattern disables title-borrowing,
        // never the feature itself.
        if (!string.IsNullOrWhiteSpace(_config.MeetingTitlePattern))
        {
            try
            {
                _titleRegex = new Regex(_config.MeetingTitlePattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "MeetingTitlePattern '{Pattern}' is not a valid regex — recordings will be named by " +
                    "channel + date rather than by a matching event title.",
                    _config.MeetingTitlePattern);
            }
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        _client.UserVoiceStateUpdated += OnVoiceStateUpdated;

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.MeetingRecordingPollIntervalMinutes));
        _logger.LogInformation(
            "MeetingRecordingScheduler started (presence-driven). VC={ChannelId}, MinPresence={Min}, " +
            "Poll={Poll}min, Retain={Retain}, TitleHint='{Pattern}'.",
            _config.MeetingVoiceChannelId, _config.MeetingRecordingMinPresenceToStart,
            _config.MeetingRecordingPollIntervalMinutes, _config.MeetingRecordingRetainCount,
            _config.MeetingTitlePattern);

        try
        {
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

                // Wait for the poll interval, but wake early if someone joins the VC.
                var wake = _wake;
                var delay = Task.Delay(interval, stoppingToken);
                await Task.WhenAny(delay, wake.Task);
                // Reset the signal for the next round (a race here at worst causes
                // one extra, harmless idempotent tick).
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        finally
        {
            _client.UserVoiceStateUpdated -= OnVoiceStateUpdated;
        }
    }

    /// <summary>Nudge the poll loop the moment someone joins the meeting VC.</summary>
    private Task OnVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        if (user is { IsBot: true }) return Task.CompletedTask;
        if (after.VoiceChannel?.Id == _config.MeetingVoiceChannelId
            && before.VoiceChannel?.Id != _config.MeetingVoiceChannelId)
        {
            _wake.TrySetResult();
        }
        return Task.CompletedTask;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var now = DateTime.UtcNow;

        // Live occupancy of the single meeting VC.
        var vc = _client.GetChannel(_config.MeetingVoiceChannelId) as SocketVoiceChannel;
        var humanCount = vc?.ConnectedUsers.Count(u => !u.IsBot) ?? 0;
        var guildId = vc?.Guild.Id ?? 0;

        await MaybeStartFromPresenceAsync(db, vc, humanCount, guildId, now, ct);
        await DriveActiveRecordingsAsync(db, humanCount, now, ct);

        await db.SaveChangesAsync(ct);
    }

    // ── 1. Start a recording when enough people are in the VC ─────────────────
    private async Task MaybeStartFromPresenceAsync(
        BotDbContext db, SocketVoiceChannel? vc, int humanCount, ulong guildId, DateTime now, CancellationToken ct)
    {
        if (vc is null || guildId == 0)
            return;

        var threshold = Math.Max(1, _config.MeetingRecordingMinPresenceToStart);
        if (humanCount < threshold)
            return;

        // Never run two recordings for the same channel at once. A row that's
        // Announced (trying to start) or Recording counts as active.
        var hasActive = await db.MeetingRecordings.AnyAsync(
            m => m.GuildId == guildId
              && (m.State == MeetingRecordingState.Announced || m.State == MeetingRecordingState.Recording),
            ct);
        if (hasActive)
            return;

        var resume = _resume is { } r && now <= r.UntilUtc ? r : ((string Title, DateTime UntilUtc)?)null;

        // Cooldown: don't immediately re-record a meeting that just finalized —
        // unless it was cut off, in which case the meeting is still going.
        if (resume is null)
        {
            var cooldownCutoff = now - StartCooldown;
            var recentlyStopped = await db.MeetingRecordings.AnyAsync(
                m => m.GuildId == guildId
                  && m.RecordingStoppedUtc != null
                  && m.RecordingStoppedUtc > cooldownCutoff,
                ct);
            if (recentlyStopped)
                return;
        }

        var (title, discordMessageId, calendarEventId, meetingStart) =
            await ResolveTitleAsync(db, guildId, vc, now, ct);
        if (resume is { } cont)
        {
            title = cont.Title.EndsWith(ContinuedSuffix, StringComparison.Ordinal)
                ? cont.Title
                : cont.Title + ContinuedSuffix;
            _resume = null;
        }

        var rec = new MeetingRecording
        {
            GuildId          = guildId,
            DiscordMessageId = discordMessageId,   // 0 when there's no concurrent event
            CalendarEventId  = calendarEventId,    // 0 when there's no concurrent event
            MeetingTitle     = title,
            MeetingStartUtc  = meetingStart,
            MeetingEndUtc    = now,                 // placeholder; only feeds the backstop cap
            JoinAtUtc        = now,
            State            = MeetingRecordingState.Announced,
            StateUpdatedUtc  = now,
            CreatedAt        = now,
        };
        db.MeetingRecordings.Add(rec);
        // Persist now so the row gets its Id (the recorder keys audio dirs on it)
        // and so a crash right after this doesn't lose the fact that we started.
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Meeting VC has {Count} members (≥{Threshold}) — starting recording '{Title}' (#{Id}).",
            humanCount, threshold, title, rec.Id);

        await PostAnnouncementAsync(rec, ct);
        // The actual recorder start is attempted here and retried by the drive
        // loop while the row is Announced (in case the recorder is briefly down).
        await TryStartRecorderAsync(rec, now, ct);
    }

    /// <summary>
    /// Name a presence-triggered recording. If a clan-source Apollo event overlaps
    /// now (within the lead-minutes buffer), borrow its title — preferring one that
    /// matches the optional title pattern — and reference it for debugging. Failing
    /// that, name the recording after the voice channel and the date.
    /// </summary>
    private async Task<(string Title, ulong DiscordMessageId, int CalendarEventId, DateTime MeetingStart)>
        ResolveTitleAsync(BotDbContext db, ulong guildId, SocketVoiceChannel vc, DateTime now, CancellationToken ct)
    {
        var sources = _config.GetAttendanceCountingSourcesList();
        var buffer = TimeSpan.FromMinutes(Math.Max(0, _config.MeetingRecordingLeadMinutes));
        var lo = now - buffer;
        var hi = now + buffer;

        var concurrent = await db.CalendarEvents
            .Where(e => e.GuildId == guildId
                     && sources.Contains(e.Source)
                     && e.StartUtc <= hi
                     && e.EndUtc >= lo)
            .OrderBy(e => e.StartUtc)
            .ToListAsync(ct);

        CalendarEvent? evt = null;
        if (_titleRegex is not null)
            evt = concurrent.FirstOrDefault(e => !string.IsNullOrEmpty(e.Title) && _titleRegex.IsMatch(e.Title));
        evt ??= concurrent.FirstOrDefault(e => !string.IsNullOrEmpty(e.Title));

        if (evt is not null)
        {
            _logger.LogInformation(
                "Naming recording after concurrent event '{Title}' (msg {Msg}).", evt.Title, evt.DiscordMessageId);
            return (evt.Title, evt.DiscordMessageId, evt.Id, evt.StartUtc);
        }

        var channelName = string.IsNullOrWhiteSpace(vc.Name) ? "Meeting" : vc.Name;
        return ($"{channelName} — {now:yyyy-MM-dd}", 0UL, 0, now);
    }

    // ── 2. Drive active rows: finish starting, then finalize when done ────────
    private async Task DriveActiveRecordingsAsync(BotDbContext db, int humanCount, DateTime now, CancellationToken ct)
    {
        var active = await db.MeetingRecordings
            .Where(m => m.State == MeetingRecordingState.Announced
                     || m.State == MeetingRecordingState.Recording)
            .ToListAsync(ct);

        // One status read per tick, taken after any start this tick, so a row that
        // just started is already the recorder's current meeting.
        var status = await _recorder.GetStatusAsync(ct);

        foreach (var rec in active)
        {
            if (rec.State == MeetingRecordingState.Announced)
            {
                // Everyone left before the recorder ever started — nothing captured.
                if (humanCount == 0)
                {
                    _logger.LogInformation(
                        "Recording '{Title}' (#{Id}) — VC emptied before recording started; cancelling.",
                        rec.MeetingTitle, rec.Id);
                    await DeleteAnnouncementAsync(rec, ct);
                    // A start that timed out on our side may still have joined.
                    if (await TryAdoptFromRecorderAsync(rec, now, ct, "cancelled-while-starting"))
                        continue;
                    Transition(rec, MeetingRecordingState.Cancelled, now);
                    continue;
                }

                // Gave up trying to reach the recorder.
                if (now - rec.StateUpdatedUtc >= StartWindow)
                {
                    await DeleteAnnouncementAsync(rec, ct);
                    if (await TryAdoptFromRecorderAsync(rec, now, ct, "start-window-closed"))
                        continue;
                    rec.ErrorMessage = "Recorder could not be started within the start window.";
                    _logger.LogWarning("Recording '{Title}' (#{Id}) never started, Failed.",
                        rec.MeetingTitle, rec.Id);
                    Transition(rec, MeetingRecordingState.Failed, now);
                    await MeetingFailureNotice.PostAsync(_client, _config, _logger, rec, "Announced", ct);
                    continue;
                }

                // Retry the start.
                await TryStartRecorderAsync(rec, now, ct);
                continue;
            }

            // Recording: the recorder auto-stops when the VC empties and remembers
            // the result. Advance as soon as it reports finalized, or as soon as it
            // reports it isn't recording this meeting at all (it restarted and lost
            // it). The backstop covers a recorder that can't be reached.
            if (rec.State == MeetingRecordingState.Recording)
            {
                var backstopAt = (rec.RecordingStartedUtc ?? rec.MeetingEndUtc) + StopBackstop;
                if (status is not null && status.Finalized.TryGetValue(rec.Id, out var reason))
                {
                    var cutOff = reason is not null && CutOffReasons.Contains(reason);
                    await FinalizeRecordingAsync(rec, now, ct, $"recorder-finalized:{reason ?? "unknown"}", resume: cutOff);
                }
                else if (status is { Ready: true } && status.RecordingId != rec.Id)
                {
                    _logger.LogWarning(
                        "Recorder is ready but not recording '{Title}' (#{Id}) (recording {Current}); " +
                        "it most likely restarted mid-meeting. Recovering the audio captured so far.",
                        rec.MeetingTitle, rec.Id, status.RecordingId?.ToString() ?? "nothing");
                    await FinalizeRecordingAsync(rec, now, ct, "recorder-lost", resume: true);
                }
                else if (now >= backstopAt)
                {
                    await FinalizeRecordingAsync(rec, now, ct, reason: "backstop");
                }
            }
        }

        await StopOrphanedRecordingAsync(db, status, active, now, ct);
    }

    /// <summary>
    /// The recorder is capturing a meeting the scheduler no longer considers
    /// active (e.g. the row was failed or cancelled while a start was still
    /// joining). Stop it so it doesn't block the next meeting, and hand any audio
    /// it captured back to its row so it still gets minutes.
    /// </summary>
    private async Task StopOrphanedRecordingAsync(
        BotDbContext db, MeetingRecorderStatus? status, List<MeetingRecording> active, DateTime now, CancellationToken ct)
    {
        if (status?.RecordingId is not { } orphanId) return;
        if (active.Any(m => m.Id == orphanId
                         && m.State is MeetingRecordingState.Announced or MeetingRecordingState.Recording))
            return;

        _logger.LogWarning("Recorder is capturing #{Id}, which is not an active recording; stopping it.", orphanId);
        string? dir = null;
        try
        {
            dir = await _recorder.StopRecordingAsync(orphanId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop orphaned recording #{Id}.", orphanId);
            return;
        }

        if (string.IsNullOrWhiteSpace(dir)) return;

        var row = await db.MeetingRecordings.FirstOrDefaultAsync(m => m.Id == orphanId, ct);
        if (row is { AudioDirPath: null, State: MeetingRecordingState.Failed or MeetingRecordingState.Cancelled })
        {
            AdoptAudio(row, dir, now, "orphan");
        }
        else
        {
            _logger.LogWarning("Orphaned recording #{Id} left audio at {Dir} with no row to adopt it.", orphanId, dir);
        }
    }

    /// <summary>
    /// For a row that is being cancelled or failed while still Announced: our
    /// start call may have timed out after the recorder actually joined. Stop
    /// that recording and, if it captured audio, carry the row on to Transcribing.
    /// Returns true when the row was adopted.
    /// </summary>
    private async Task<bool> TryAdoptFromRecorderAsync(MeetingRecording rec, DateTime now, CancellationToken ct, string reason)
    {
        string? dir;
        try
        {
            dir = await _recorder.StopRecordingAsync(rec.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort recorder stop for #{Id} failed.", rec.Id);
            return false;
        }

        if (string.IsNullOrWhiteSpace(dir)) return false;
        rec.RecordingStartedUtc ??= rec.JoinAtUtc;
        AdoptAudio(rec, dir, now, reason);
        return true;
    }

    private void AdoptAudio(MeetingRecording rec, string dir, DateTime now, string reason)
    {
        rec.RecordingStoppedUtc = now;
        rec.AudioDirPath = dir;
        rec.ErrorMessage = null;
        Transition(rec, MeetingRecordingState.Transcribing, now);
        _logger.LogInformation("Adopted audio for '{Title}' (#{Id}, {Reason}); audio dir: {Dir}.",
            rec.MeetingTitle, rec.Id, reason, dir);
    }

    /// <summary>
    /// Ask the recorder to join and begin capturing. On success advance to
    /// Recording; on failure leave the row Announced to retry on the next poll
    /// until the start window closes.
    /// </summary>
    private async Task TryStartRecorderAsync(MeetingRecording rec, DateTime now, CancellationToken ct)
    {
        try
        {
            var expectedStop = now + ExpectedMeetingDuration;
            await _recorder.StartRecordingAsync(new MeetingRecorderStartContext(
                rec.Id, rec.GuildId, _config.MeetingVoiceChannelId, rec.MeetingTitle, expectedStop), ct);
            rec.RecordingStartedUtc = now;
            rec.ErrorMessage = null;
            Transition(rec, MeetingRecordingState.Recording, now);
            _logger.LogInformation("Recorder joined for '{Title}' (#{Id}).", rec.MeetingTitle, rec.Id);
        }
        catch (Exception ex)
        {
            rec.ErrorMessage = Truncate(ex.Message, 800);
            _logger.LogError(ex,
                "Failed to start recorder for '{Title}' (#{Id}); will retry until the start window closes.",
                rec.MeetingTitle, rec.Id);
        }
    }

    /// <summary>
    /// Ask the recorder for the finalized audio dir and advance to Transcribing.
    /// If the recorder returns nothing, reconcile against the shared volume so a
    /// recording with audio on disk is NEVER wrongly failed. Only when no audio
    /// exists anywhere is the row failed.
    /// </summary>
    private async Task FinalizeRecordingAsync(
        MeetingRecording rec, DateTime now, CancellationToken ct, string reason, bool resume = false)
    {
        if (resume)
        {
            _resume = (rec.MeetingTitle, now + ResumeWindow);
            // Run the next tick right away so the continuation starts promptly.
            _wake.TrySetResult();
        }

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
            _logger.LogWarning("Recording for '{Title}' (#{Id}) produced no audio ({Reason}), Failed.",
                rec.MeetingTitle, rec.Id, reason);
            await DeleteAnnouncementAsync(rec, ct);
            Transition(rec, MeetingRecordingState.Failed, now);
            await MeetingFailureNotice.PostAsync(_client, _config, _logger, rec, "Recording", ct);
            return;
        }

        await DeleteAnnouncementAsync(rec, ct);
        AdoptAudio(rec, dir, now, reason);
    }

    /// <summary>
    /// Look on the shared audio volume for a finalized recording directory for
    /// this meeting id (meeting_{id}_*) whose manifest.json reports real audio.
    /// Returns the directory path, or null if none. This makes the pipeline robust
    /// to the recorder losing its in-memory state (restart) after it had already
    /// written the audio + manifest.
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
                var promoted = false;
                if (!File.Exists(manifestPath))
                {
                    // The recorder was killed before its final manifest; fall back to
                    // its latest checkpoint (marked partial, so the transcriber won't
                    // trust its utterance timing).
                    var partialPath = Path.Combine(dir, "manifest.partial.json");
                    if (!File.Exists(partialPath)) continue;
                    manifestPath = partialPath;
                    promoted = true;
                }

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
                        if (promoted)
                        {
                            File.Copy(manifestPath, Path.Combine(dir, "manifest.json"), overwrite: false);
                            _logger.LogWarning(
                                "Recording #{Id} had no final manifest; promoted its checkpoint in {Dir}.",
                                meetingRecordingId, dir);
                        }
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
            $"🔴 **Recording notice** — this meeting is being recorded by ClanGuard to generate " +
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
