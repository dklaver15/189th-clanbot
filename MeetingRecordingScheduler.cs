using System.Collections.Concurrent;
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
///      MeetingTitlePattern (a regex, so "189th Monthly Meeting" still matches
///      "Monthly Meeting – June"), restricted to the attendance-counting
///      sources ("Clan"). No day/time is ever hardcoded.
///   2. Upserts a MeetingRecording row for it, recomputing JoinAtUtc when the
///      event is rescheduled and refreshing the title on rename (pre-recording
///      states only).
///   3. Reconciles cancellations — if a scheduled meeting's CalendarEvent has
///      disappeared (Apollo event deleted), the recording is Cancelled.
///   4. Fires the recorder at JoinAtUtc (= StartUtc − lead): posts the
///      recording-consent notice, then asks the controller to start. Stops it
///      a short buffer after MeetingEndUtc and hands off to the transcription
///      stage.
///
/// Restart-safe: all state lives in the MeetingRecordings table (mirrors the
/// reminder handlers and BotState catch-up pattern), so a redeploy mid-cycle
/// resumes from whatever state each row was last in.
///
/// Feature-gated: a no-op unless MeetingRecordingEnabled is true AND both
/// MeetingVoiceChannelId and MeetingTitlePattern are configured.
/// </summary>
public class MeetingRecordingScheduler : BackgroundService
{
    /// <summary>How far past EndUtc we keep recording before stopping — catches meetings that run long.</summary>
    private static readonly TimeSpan StopBuffer = TimeSpan.FromMinutes(5);

    /// <summary>Don't bother scheduling meetings further out than this; we'll pick them up on a later poll.</summary>
    private static readonly TimeSpan SchedulingHorizon = TimeSpan.FromDays(60);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly IMeetingRecorderController _recorder;
    private readonly ILogger<MeetingRecordingScheduler> _logger;
    private readonly BotConfig _config;

    private Regex? _titleRegex;

    // Remembers the recording-notice message posted per recording, so it can be
    // deleted when recording stops (keeps the channel clean). In-memory by design:
    // a bot restart mid-meeting simply leaves that one notice in place — a
    // cosmetic edge case, not worth a schema migration.
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
            // IgnoreCase + Compiled — the pattern is reused every poll. Authors can still
            // embed inline flags (e.g. "(?i)") but IgnoreCase is the sane default for titles.
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

        // Same readiness gate + startup settle as MeetingAttendanceSnapshotService:
        // let Apollo backfill / parser worker populate CalendarEvents before we query.
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
                // A bad poll must never kill the loop — log and keep going.
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

        // Pull candidate future events in the horizon for the counting sources, then
        // apply the title regex in memory (regex can't be translated to SQL).
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

        var existing = await db.MeetingRecordings
            .FirstOrDefaultAsync(m => m.DiscordMessageId == match.DiscordMessageId
                                   && m.State != MeetingRecordingState.Cancelled
                                   && m.State != MeetingRecordingState.Failed
                                   && m.State != MeetingRecordingState.Posted
                                   && m.State != MeetingRecordingState.Pruned, ct);

        if (existing is null)
        {
            db.MeetingRecordings.Add(new MeetingRecording
            {
                GuildId         = match.GuildId,
                DiscordMessageId = match.DiscordMessageId,
                CalendarEventId = match.Id,
                MeetingTitle    = match.Title,
                MeetingStartUtc = match.StartUtc,
                MeetingEndUtc   = match.EndUtc,
                JoinAtUtc       = joinAt,
                State           = MeetingRecordingState.Scheduled,
                StateUpdatedUtc = now,
                CreatedAt       = now,
            });
            _logger.LogInformation(
                "Scheduled recording for '{Title}' starting {Start:yyyy-MM-dd HH:mm} UTC (join at {Join:HH:mm}).",
                match.Title, match.StartUtc, joinAt);
            return;
        }

        // Reschedule / rename tracking — only while we haven't started capturing yet.
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

                // If we'd already announced but the meeting moved back into the future,
                // drop to Scheduled so we re-announce at the new (later) join time.
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

    // ── 3. Announce → start → stop → hand off ─────────────────────────────────
    private async Task DriveStateMachineAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        var active = await db.MeetingRecordings
            .Where(m => m.State == MeetingRecordingState.Scheduled
                     || m.State == MeetingRecordingState.Announced
                     || m.State == MeetingRecordingState.Recording)
            .ToListAsync(ct);

        foreach (var rec in active)
        {
            var windowEnd = rec.MeetingEndUtc + StopBuffer;

            // Missed the whole window without ever starting — give up.
            if (rec.State is MeetingRecordingState.Scheduled or MeetingRecordingState.Announced
                && now >= windowEnd)
            {
                rec.ErrorMessage = "Recording window elapsed without a successful start.";
                _logger.LogWarning("Recording for '{Title}' (#{Id}) missed its window — Failed.",
                    rec.MeetingTitle, rec.Id);
                await DeleteAnnouncementAsync(rec, ct);
                Transition(rec, MeetingRecordingState.Failed, now);
                continue;
            }

            // Announce once, at lead time. Falls through to the start attempt below
            // so the recorder is asked to join in the same pass.
            if (rec.State == MeetingRecordingState.Scheduled && now >= rec.JoinAtUtc && now < windowEnd)
            {
                await PostAnnouncementAsync(rec, ct);
                Transition(rec, MeetingRecordingState.Announced, now);
            }

            // Start (retried each poll while Announced and still within the window).
            if (rec.State == MeetingRecordingState.Announced && now < windowEnd)
            {
                try
                {
                    await _recorder.StartRecordingAsync(new MeetingRecorderStartContext(
                        rec.Id, rec.GuildId, _config.MeetingVoiceChannelId, rec.MeetingTitle, windowEnd), ct);
                    rec.RecordingStartedUtc = now;
                    rec.ErrorMessage = null;
                    Transition(rec, MeetingRecordingState.Recording, now);
                }
                catch (Exception ex)
                {
                    // Stay Announced and retry next poll until the window closes.
                    rec.ErrorMessage = Truncate(ex.Message, 800);
                    _logger.LogError(ex,
                        "Failed to start recorder for '{Title}' (#{Id}); will retry until {End:HH:mm} UTC.",
                        rec.MeetingTitle, rec.Id, windowEnd);
                }
                continue;
            }

            // Stop a buffer past EndUtc and hand off to the transcription stage.
            if (rec.State == MeetingRecordingState.Recording && now >= windowEnd)
            {
                try
                {
                    var dir = await _recorder.StopRecordingAsync(rec.Id, ct);
                    rec.RecordingStoppedUtc = now;
                    rec.AudioDirPath = dir;
                    await DeleteAnnouncementAsync(rec, ct);
                    // With the placeholder recorder dir is null, so the row simply
                    // waits in Transcribing until the STT worker exists — expected.
                    Transition(rec, MeetingRecordingState.Transcribing, now);
                    _logger.LogInformation("Stopped recording '{Title}' (#{Id}); audio dir: {Dir}.",
                        rec.MeetingTitle, rec.Id, dir ?? "(none)");
                }
                catch (Exception ex)
                {
                    rec.ErrorMessage = Truncate(ex.Message, 800);
                    _logger.LogError(ex, "Failed to stop recorder for '{Title}' (#{Id}).",
                        rec.MeetingTitle, rec.Id);
                }
            }
        }
    }

    private async Task PostAnnouncementAsync(MeetingRecording rec, CancellationToken ct)
    {
        // Prefer the configured announcement channel; fall back to the meeting VC's
        // own text chat (voice channels are IMessageChannel in modern Discord).
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
            // Best-effort: a failed notice must never block recording. Degrade to
            // "recorded but unannounced" rather than failing the whole pipeline.
            _logger.LogWarning(ex,
                "Failed to post recording notice for '{Title}' to channel {ChannelId} — recording will proceed unannounced.",
                rec.MeetingTitle, channelId);
        }
    }

    // Best-effort: remove the recording-notice message once recording ends, to
    // keep the channel clean. Never throws — a failed delete just leaves the notice.
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
