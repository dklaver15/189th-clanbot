using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Abstraction over "make a bot join the meeting VC and capture per-user audio."
///
/// Discord.Net cannot do this — its voice stack only implements the
/// xsalsa20_poly1305 encryption mode Discord discontinued on 2024-11-18, and
/// its receive path has never worked reliably — so the real implementation is
/// a separate sidecar process (@discordjs/voice) reached over localhost HTTP.
/// The scheduler depends only on this interface, so the sidecar can be built
/// and swapped in later by changing one DI registration; nothing in the
/// scheduling brain changes.
///
/// Until the sidecar exists, <see cref="LoggingMeetingRecorderController"/> is
/// registered as a no-op that just logs. With it in place the scheduler runs
/// end to end (discovery, reschedule tracking, cancellation, announcement,
/// state transitions) — there's simply no audio file at the end, so rows park
/// in Transcribing awaiting the (not-yet-built) STT step. That's intentional:
/// it lets the scheduling logic be exercised against real CalendarEvent data
/// well before any voice plumbing is wired.
/// </summary>
public interface IMeetingRecorderController
{
    /// <summary>
    /// Tell the recorder to join <paramref name="ctx"/>.VoiceChannelId and begin
    /// capturing per-user tracks. Should return once recording has *started*
    /// (not when it ends). Throws on failure — the scheduler catches and parks
    /// the row in Announced to retry on the next poll until the window closes.
    /// </summary>
    Task StartRecordingAsync(MeetingRecorderStartContext ctx, CancellationToken ct);

    /// <summary>
    /// Tell the recorder to leave the VC and flush per-user tracks + a manifest
    /// to the shared volume. Returns the directory the audio was written to, or
    /// null if the recorder reports nothing was captured.
    /// </summary>
    Task<string?> StopRecordingAsync(int meetingRecordingId, CancellationToken ct);

    /// <summary>
    /// What the recorder is doing right now: whether it is ready, which meeting it
    /// is recording (or joining), and which meetings it has already finalized and
    /// why. Lets the scheduler advance Recording → Transcribing as soon as the
    /// recorder finishes, and notice immediately when a restart made the recorder
    /// lose a meeting, rather than waiting for the backstop. Returns null when the
    /// recorder can't be reached, so callers treat that as "unknown".
    /// </summary>
    Task<MeetingRecorderStatus?> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// A member opted out mid-recording: stop capturing them and delete what was
    /// already captured for this meeting. Throws when the recorder isn't
    /// recording that meeting or can't be reached.
    /// </summary>
    Task ExcludeUserAsync(int meetingRecordingId, ulong userId, CancellationToken ct);
}

/// <summary>Everything the recorder needs to start a capture.</summary>
public sealed record MeetingRecorderStartContext(
    int MeetingRecordingId,
    ulong GuildId,
    ulong VoiceChannelId,
    string MeetingTitle,
    DateTime ExpectedStopUtc,
    IReadOnlyCollection<ulong>? ExcludedUserIds = null);

/// <summary>
/// Snapshot of the recorder's /health. <see cref="Finalized"/> maps each recently
/// finalized meeting id to the recorder's stop reason (e.g. "vc-empty",
/// "disconnected", "shutdown").
/// </summary>
public sealed record MeetingRecorderStatus(
    bool Ready,
    int? RecordingId,
    IReadOnlyDictionary<int, string?> Finalized);

/// <summary>
/// Placeholder recorder used until the @discordjs/voice sidecar is built. Logs
/// what it *would* do and reports "no audio captured," so the scheduler's
/// state machine advances correctly without pretending audio exists.
/// </summary>
public sealed class LoggingMeetingRecorderController(
    ILogger<LoggingMeetingRecorderController> logger) : IMeetingRecorderController
{
    public Task StartRecordingAsync(MeetingRecorderStartContext ctx, CancellationToken ct)
    {
        logger.LogWarning(
            "[recorder-placeholder] Would START recording '{Title}' in VC {ChannelId} " +
            "(recording #{Id}); stop expected ~{Stop:yyyy-MM-dd HH:mm} UTC. " +
            "No sidecar wired — no audio will be captured.",
            ctx.MeetingTitle, ctx.VoiceChannelId, ctx.MeetingRecordingId, ctx.ExpectedStopUtc);
        return Task.CompletedTask;
    }

    public Task<string?> StopRecordingAsync(int meetingRecordingId, CancellationToken ct)
    {
        logger.LogWarning(
            "[recorder-placeholder] Would STOP recording #{Id}. No sidecar wired — returning no audio.",
            meetingRecordingId);
        return Task.FromResult<string?>(null);
    }

    // No sidecar, so there is no status to report — the scheduler falls back to
    // its backstop, which is correct since there's no real recording to advance.
    public Task<MeetingRecorderStatus?> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult<MeetingRecorderStatus?>(null);

    public Task ExcludeUserAsync(int meetingRecordingId, ulong userId, CancellationToken ct)
    {
        logger.LogWarning(
            "[recorder-placeholder] Would EXCLUDE user {UserId} from recording #{Id}. No sidecar wired.",
            userId, meetingRecordingId);
        return Task.CompletedTask;
    }
}
