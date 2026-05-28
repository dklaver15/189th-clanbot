namespace ClanGuardBot.Models;

/// <summary>
/// One row per recorded (or to-be-recorded) meeting occurrence.
///
/// ── Why keyed on DiscordMessageId, not a fixed schedule ──
/// The monthly meeting is an Apollo event. Apollo posts a *new message* for
/// each occurrence, so DiscordMessageId is the natural stable key for "this
/// specific meeting." Recurrence is therefore free — next month's meeting is
/// a different message → a new row — and the scheduler never has to reason
/// about RRULEs or hardcoded days/times.
///
/// ── Reschedule / rename handling ──
/// CalendarEvent rows are updated in place when Apollo revises an event (see
/// ApolloEvent / ApolloEventHandler). MeetingRecordingScheduler re-reads the
/// matched CalendarEvent on every poll, so a moved StartUtc or a tweaked
/// Title is picked up automatically and JoinAtUtc is recomputed — as long as
/// the row is still in a pre-recording state (Scheduled/Announced). Once we're
/// actually Recording we stop chasing the event time.
///
/// ── Cancellation ──
/// When an Apollo event is deleted, its live CalendarEvent row goes away. The
/// scheduler treats "no CalendarEvent with this DiscordMessageId" as a
/// cancellation and parks the recording in Cancelled (terminal) — but only if
/// it hasn't already started recording.
///
/// ── Retention ──
/// MeetingRecordingState.Pruned marks an occurrence whose *audio* has been
/// deleted by the keep-last-N policy. The row (and its transcript/minutes
/// paths, which are tiny) is preserved so meeting history survives even after
/// the audio is gone.
/// </summary>
public class MeetingRecording
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    /// <summary>
    /// Apollo post message ID — the stable per-occurrence key. Survives event
    /// revisions (Apollo edits the same message) and is what we de-dupe on so
    /// a single meeting never spawns two recording rows.
    /// </summary>
    public ulong DiscordMessageId { get; set; }

    /// <summary>
    /// CalendarEvent.Id of the matched meeting at the time we last reconciled.
    /// Kept for joins/debugging; DiscordMessageId is the durable key (a delete +
    /// recreate would change this Id but not the message id).
    /// </summary>
    public int CalendarEventId { get; set; }

    /// <summary>Snapshot of the meeting title — updated on rename while still pre-recording.</summary>
    public string MeetingTitle { get; set; } = string.Empty;

    /// <summary>Snapshot of the event window — updated on reschedule while still pre-recording.</summary>
    public DateTime MeetingStartUtc { get; set; }
    public DateTime MeetingEndUtc { get; set; }

    /// <summary>
    /// When the recorder should join: MeetingStartUtc minus the configured lead.
    /// Recomputed whenever MeetingStartUtc moves (pre-recording only).
    /// </summary>
    public DateTime JoinAtUtc { get; set; }

    public MeetingRecordingState State { get; set; } = MeetingRecordingState.Scheduled;

    /// <summary>Stamped on every state transition — surfaced on /health and used to age out stuck rows.</summary>
    public DateTime StateUpdatedUtc { get; set; }

    public DateTime? RecordingStartedUtc { get; set; }
    public DateTime? RecordingStoppedUtc { get; set; }

    /// <summary>
    /// Directory on the shared volume holding the per-user audio tracks + manifest
    /// the recorder sidecar produced. Null until recording starts. The keep-last-N
    /// retention sweep deletes this directory when the row is Pruned.
    /// </summary>
    public string? AudioDirPath { get; set; }

    /// <summary>Path to the merged, speaker-labelled transcript file. Null until transcription completes.</summary>
    public string? TranscriptPath { get; set; }

    /// <summary>
    /// The merged, speaker-labelled transcript text, stored in the DB so it
    /// survives the keep-last-N audio prune (which deletes AudioDirPath, and
    /// with it the on-disk transcript file). Null until transcription completes.
    /// </summary>
    public string? TranscriptText { get; set; }

    /// <summary>
    /// The final minutes markdown produced by Claude. Stored in the DB so
    /// meeting history persists after the audio is pruned. Null until generated.
    /// </summary>
    public string? MinutesText { get; set; }

    /// <summary>
    /// JSON array of action items parsed from Claude's output
    /// (e.g. [{"owner":"...","task":"...","next":"..."}]). Null until generated
    /// or if none were extracted.
    /// </summary>
    public string? ActionItemsJson { get; set; }

    /// <summary>Message ID of the posted minutes, for idempotency / later edits. Null until posted.</summary>
    public ulong? MinutesMessageId { get; set; }

    /// <summary>Last failure detail (truncated to 800 chars), cleared on the next successful transition.</summary>
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Lifecycle of a single meeting recording. Forward-only except for the two
/// terminal escape hatches (Cancelled, Failed). Each component owns the
/// transitions into the state it produces:
///
///   Scheduled    → set by the scheduler when a matching future meeting is found
///   Announced    → scheduler has posted the recording-consent notice
///   Recording    → recorder sidecar is in the VC capturing per-user tracks
///   Transcribing → audio captured; STT (local Whisper) in progress
///   Summarized   → Claude has produced minutes + action items
///   Posted       → minutes posted to the channel (success terminal for this occurrence)
///   Pruned       → audio deleted by keep-last-N retention (row + transcript kept)
///   Cancelled    → meeting was cancelled before recording began (terminal)
///   Failed       → unrecoverable error; ErrorMessage explains (terminal)
/// </summary>
public enum MeetingRecordingState
{
    Scheduled    = 1,
    Announced    = 2,
    Recording    = 3,
    Transcribing = 4,
    Summarized   = 5,
    Posted       = 6,
    Pruned       = 7,
    Cancelled    = 8,
    Failed       = 9,
}