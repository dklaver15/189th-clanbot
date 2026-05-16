namespace ClanGuardBot.Models;

/// <summary>
/// Tracks a single user's activity within a guild.
/// One record per user per guild.
/// </summary>
public class UserActivity
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>When the user last joined a voice channel (null if not currently in voice).</summary>
    public DateTime? VoiceJoinedAt { get; set; }
}

/// <summary>
/// A single recorded message event.
/// </summary>
public class MessageEvent
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// A single recorded voice session (join → leave).
/// </summary>
public class VoiceSession
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }

    /// <summary>The voice channel ID (for filtering by specific channels like Events).</summary>
    public ulong? ChannelId { get; set; }

    /// <summary>The voice channel name at time of join.</summary>
    public string? ChannelName { get; set; }

    /// <summary>
    /// Parent category ID at time of join. Captured so event-attendance queries
    /// can count time spent in any VC under the EVENTS category — including
    /// temporary event VCs that may be deleted before the attendance snapshot
    /// runs. Null for sessions recorded before this column existed; those rows
    /// fall back to the legacy single-channel check via EventsVoiceChannelId.
    /// </summary>
    public ulong? CategoryId { get; set; }
}

/// <summary>
/// Tracks when a user was assigned their current rank role.
/// Only the latest record per user/guild matters for "time in rank".
///
/// ── Seed fields (one-time spreadsheet backfill) ──
/// EventsAttendedAtRankBeforeBot + SeedAppliedAt exist for the transition
/// from the manual promotion spreadsheet to bot-tracked event attendance.
///
/// EventsAttendedAtRankBeforeBot is the count of qualifying events the user
/// had already accumulated at their current rank when the seed was applied.
/// SeedAppliedAt marks the moment the seed was applied; bot-tracked events
/// are only counted *after* SeedAppliedAt to avoid double-counting events
/// already reflected in the seed number.
///
/// Both fields MUST be reset (to 0 and null) whenever RankName changes — a
/// new rank means a fresh count, not an inheritance of the previous rank's
/// seed. Any code path that mutates RankHistory on rank change is responsible
/// for zeroing these fields at the same time (RosterExportService detection,
/// RankTrackingHandler realtime updates, promote/demote command flows).
/// </summary>
public class RankHistory
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>The rank role name (e.g. "PVT", "SGT").</summary>
    public string RankName { get; set; } = string.Empty;

    /// <summary>When this rank was first detected on the user.</summary>
    public DateTime AssignedAt { get; set; }

    /// <summary>
    /// One-time backfill of qualifying events already attended at this rank
    /// before the bot became authoritative. Applied via /seed-promotion-credit
    /// from the "Seed Events" column on the roster sheet. Reset to 0 on rank
    /// change. Default 0 means "no seed has been applied."
    /// </summary>
    public int EventsAttendedAtRankBeforeBot { get; set; }

    /// <summary>
    /// When the seed above was applied. Bot-tracked events (EventAttendance
    /// rows) with EventEndUtc < SeedAppliedAt are NOT counted again, since
    /// they are assumed to already be included in the seed number from the
    /// spreadsheet. Null = no seed has ever been applied at this rank, so
    /// bot-tracked counting falls back to counting from AssignedAt forward.
    /// </summary>
    public DateTime? SeedAppliedAt { get; set; }
}

/// <summary>
/// Tracks when a user was assigned the AWOL role so we know
/// when the 2-day grace period expires.
///
/// ── Notification retry policy ──
/// LastNotificationAttemptUtc is stamped on every notification attempt
/// (whether it succeeds or fails). Combined with the "give up after 7
/// days" rule in AwolCheckService, this prevents records from looping
/// in the pending-notification queue indefinitely when the bot can't
/// successfully post (channel deleted, permissions revoked, etc.). See
/// AwolCheckService for the full retry policy.
/// </summary>
public class AwolRecord
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>When the AWOL role was assigned.</summary>
    public DateTime AssignedAt { get; set; }

    /// <summary>Whether the HQ notification has already been posted.</summary>
    public bool NotificationSent { get; set; }

    /// <summary>When the HQ notification was posted (null if not yet).</summary>
    public DateTime? NotificationSentAt { get; set; }

    /// <summary>
    /// Most recent attempt to post the HQ notification, regardless of outcome.
    /// Null = no attempt has ever been made (typical for fresh records before
    /// the grace period elapses). Stamped on every attempt so the give-up
    /// policy in AwolCheckService can identify records that have been tried
    /// repeatedly without success.
    ///
    /// Records with NotificationSent=false AND LastNotificationAttemptUtc
    /// non-null AND AssignedAt older than the give-up window (7 days) get
    /// auto-resolved as "given up" so they stop occupying the pending queue.
    /// If the underlying problem is later fixed and the user is still AWOL,
    /// a fresh AwolRecord will be created on the next AWOL check cycle.
    /// </summary>
    public DateTime? LastNotificationAttemptUtc { get; set; }
}

/// <summary>
/// Persists a pending ticket reminder so it survives bot restarts.
/// Removed once the reminder fires or is cancelled.
/// </summary>
public class TicketReminder
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong ChannelId { get; set; }

    /// <summary>When the ticket channel was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the reminder should fire.</summary>
    public DateTime ReminderAt { get; set; }
}

/// <summary>
/// Persists a pending guest reminder so it survives bot restarts.
/// When a new user joins and receives the Guest role, a record is created.
/// After the configured delay, if they still have the Guest role (haven't
/// created a ticket), a nudge is posted in general chat.
/// Removed once the reminder fires or is no longer needed.
/// </summary>
public class GuestReminder
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>When the user joined the server.</summary>
    public DateTime JoinedAt { get; set; }

    /// <summary>When the reminder should fire.</summary>
    public DateTime ReminderAt { get; set; }
}

/// <summary>
/// Persists a pending onboarding step reminder so it survives bot restarts.
/// Created when a Guest completes an onboarding step (rules, platoon, gamertag)
/// but hasn't yet created a ticket. Fires after 24 hours if they still have
/// the Guest role. Only one record per user/guild at a time.
/// </summary>
public class OnboardingReminder
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Which onboarding step triggered this reminder (e.g. "rules", "platoon", "gamertag").</summary>
    public string TriggerStep { get; set; } = string.Empty;

    /// <summary>When the onboarding step was completed.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the reminder should fire.</summary>
    public DateTime ReminderAt { get; set; }
}

/// <summary>
/// Maps a calendar entry back to either a Discord message (Apollo clan events)
/// or a bot-generated comp division event. Used to keep Google Calendar in sync
/// when Apollo edits or deletes an event post, and to deduplicate on restart.
/// </summary>
public class CalendarEvent
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>
    /// Discord message ID from the Apollo bot post.
    /// Set to 0 for comp division events which are not tied to a message.
    /// </summary>
    public ulong DiscordMessageId { get; set; }

    /// <summary>The Google Calendar event ID returned by the API (used for update/delete).</summary>
    public string CalendarEventId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    /// <summary>"Clan" for Apollo-sourced events, "CompDiv" for /comp-event entries.</summary>
    public string Source { get; set; } = "Clan";

    public DateTime CreatedAt { get; set; }

    public string Description { get; set; } = "";

    /// <summary>
    /// When EventAttendanceSnapshotService last attempted to snapshot this
    /// event. Stamped on every attempt regardless of whether anyone qualified.
    /// Null means the event has never been processed yet.
    ///
    /// ── Why this column exists ──
    /// Before this column, the snapshot service determined "already processed"
    /// by checking whether *any* EventAttendance row referenced the event.
    /// That check fails open in the case where an event finishes and zero
    /// users qualify — no rows get written, and the "already processed" check
    /// keeps returning false, so the event gets re-processed on every 5-minute
    /// sweep forever. The Helldivers event on 2026-04-25 demonstrated this:
    /// hours of identical "0 attendee(s)" log lines because the event was
    /// stuck in a perpetual no-qualifiers state until its EndUtc was extended.
    ///
    /// With this column, the service stamps LastSnapshotAttemptUtc on each
    /// pass, then a "stale enough to stop retrying" check kicks in once the
    /// event is far enough in the past that further retries are unlikely to
    /// produce different results. See EventAttendanceSnapshotService for the
    /// retry policy.
    /// </summary>
    public DateTime? LastSnapshotAttemptUtc { get; set; }
}

/// <summary>
/// Durable record of a member's attendance at a single clan event. One row per
/// (guild, user, event) tuple with AttendedMinutes above the configured threshold.
///
/// ── Why this table exists ──
/// CalendarEvents rows get deleted when their source Apollo Discord message is
/// removed from #events (either manually or by Apollo's own auto-cleanup). That
/// made CalendarEvents unsuitable as the source of truth for past attendance:
/// past events vanish, and with them the data any attendance query would need.
/// This table snapshots attendance per event shortly after the event ends, so
/// the historical record persists even after the originating CalendarEvent is
/// gone.
///
/// ── Denormalized event times ──
/// We copy EventStartUtc/EventEndUtc onto each row rather than keeping them as
/// a foreign-key relationship. That's the whole point — if CalendarEvent.Id=42
/// gets deleted tomorrow, attendance rows that referenced it would still need
/// to survive. The CalendarEventId is kept for traceability but is not a FK.
/// </summary>
public class EventAttendance
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>
    /// Internal CalendarEvents.Id this attendance was computed from. Not a FK —
    /// the CalendarEvent row may be gone by the time you query this. Kept for
    /// traceability / debugging only.
    /// </summary>
    public int CalendarEventId { get; set; }

    /// <summary>
    /// Snapshot of the user's display name at the moment attendance was
    /// recorded. Captures SocketGuildUser.DisplayName (server nickname →
    /// global name → underlying username, in that order of preference) so
    /// rank-prefixed clan nicknames like "MAJ.xAP3RONINx" survive in the
    /// attendance log even after the user leaves the server.
    ///
    /// ── Why this exists ──
    /// /attendance and any other attendance reader needs to render a human
    /// name. For users still in the guild, a Discord mention &lt;@id&gt; works
    /// great — the client substitutes the live display name. For users who
    /// have left the guild between event time and read time, &lt;@id&gt;
    /// renders as a raw ID with a "you don't have access to this link"
    /// error if clicked. This field is the fallback identifier in that case.
    ///
    /// ── Lifecycle ──
    /// Populated at write time by EventAttendanceSnapshotService (looking up
    /// the SocketGuildUser at snapshot time, falling back to UserActivity
    /// for the rare case where the user left between event end and snapshot)
    /// and by EventCreditCommandHandler (from the SocketGuildUser supplied
    /// to /add-event-credit). Empty string for rows written before this
    /// column existed; the migration that added the column also runs a
    /// one-shot backfill from UserActivity.Username for those rows.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Denormalized copy of CalendarEvent.StartUtc at snapshot time.</summary>
    public DateTime EventStartUtc { get; set; }

    /// <summary>Denormalized copy of CalendarEvent.EndUtc at snapshot time.</summary>
    public DateTime EventEndUtc { get; set; }

    /// <summary>
    /// Cumulative minutes the user spent in the events VC during the buffered
    /// event window (event.Start - buffer .. event.End + buffer). Only rows
    /// meeting the configured minimum are written; this value is stored mainly
    /// for auditing / "why did they qualify" questions.
    /// </summary>
    public int AttendedMinutes { get; set; }

    /// <summary>When this attendance row was snapshotted by the background service.</summary>
    public DateTime RecordedAt { get; set; }
}

/// <summary>
/// Singleton row holding cross-cutting bot state that needs to survive
/// restarts. Logically a key/value blob, but typed for clarity and for EF
/// Core's benefit.
///
/// ── Why this exists ──
/// Some operations need a "did this happen since the last redeploy" answer
/// that can't be derived from other tables. The motivating example:
/// AutoPromotionService runs once per day at a configured UTC hour. If the
/// container redeploys 5 minutes after that hour, the service starts up,
/// computes "next run" = today's run hour (which is in the past), pushes it
/// to tomorrow, and silently skips today's cycle. Without state tracking,
/// there's no way to know on startup whether today's run already completed
/// in a previous container instance.
///
/// ── How it's used ──
/// Single row, Id = 1 by convention. AutoPromotionService stamps
/// LastAutoPromotionCompletedUtc on every successful cycle (including
/// dry-run cycles, since the cycle still ran, just produced no real
/// promotions). At startup, if we're past today's run hour AND
/// LastAutoPromotionCompletedUtc is null OR before today's run hour, we
/// run a catch-up immediately.
///
/// Future schedulers (RosterExportService, etc.) can add their own
/// columns here without a separate table per service.
/// </summary>
public class BotState
{
    public int Id { get; set; }

    /// <summary>
    /// Timestamp of the last successfully-completed AutoPromotionService cycle.
    /// Null if no cycle has ever completed (fresh DB).
    ///
    /// "Successfully completed" means RunAutoPromotionAsync finished without
    /// throwing. Individual member-level errors inside the cycle don't
    /// invalidate the timestamp — those get logged and skipped, the cycle
    /// itself still completes.
    /// </summary>
    public DateTime? LastAutoPromotionCompletedUtc { get; set; }

    /// <summary>
    /// Discord message ID of the persistent "Apply for Officer" button message
    /// in the configured instructions channel. Set by /setup-officer-app;
    /// consulted on re-run for idempotency. Null until the button has been
    /// posted for the first time. If the message is deleted manually, re-run
    /// the slash command with force:true to repost.
    /// </summary>
    public ulong? OfficerAppButtonMessageId { get; set; }

    /// <summary>
    /// Discord message ID of the persistent "Enter Gamertags" button message
    /// in the configured instructions channel. Set by /setup-gamertags;
    /// consulted on re-run for idempotency. Null until the button has been
    /// posted for the first time. If the message is deleted manually, re-run
    /// the slash command with force:true to repost.
    /// </summary>
    public ulong? GamertagButtonMessageId { get; set; }
}

/// <summary>
/// One row per slash-command invocation. Written by
/// CommandUsageTrackingHandler on every SlashCommandExecuted event;
/// pruned to the last 90 days by CommandUsagePruneService. Captures who
/// ran what, when, and (for an explicit allowlist of choice/boolean
/// params) the values they passed.
///
/// ── Out of scope ──
/// This table is descriptive only. It is NOT consumed by AutoPromotionService
/// or any other activity-scoring code path; running /command-catalog ten
/// times in a row should not move a member's promotion eligibility.
///
/// ── Sensitive parameter values are dropped at write time ──
/// /promote, /setnick, /demote, and the user-picker commands are intentionally
/// absent from CommandUsageTrackingHandler.ParameterAllowlist, so their
/// option values never reach this table — only the command name + invoker.
/// The audit story for those commands lives elsewhere (rank history, role
/// change events, log files).
/// </summary>
public class CommandUsage
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }       // 0 for DM invocations
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public ulong ChannelId { get; set; }     // 0 for DM invocations

    /// <summary>Top-level command name, e.g. "command-catalog", "invite".</summary>
    public string CommandName { get; set; } = string.Empty;

    /// <summary>
    /// Space-separated subcommand path for grouped commands, e.g. "create"
    /// for /invite create or "stats" for /leads stats. Null for flat
    /// commands like /command-catalog.
    /// </summary>
    public string? SubcommandPath { get; set; }

    /// <summary>
    /// JSON object of {param: value} for allowlisted leaf options only.
    /// Null when the command has no allowlisted params or the user passed
    /// none. See CommandUsageTrackingHandler.ParameterAllowlist for the
    /// exhaustive list of (command, param) pairs whose values are stored.
    /// </summary>
    public string? Parameters { get; set; }

    public DateTime ExecutedAt { get; set; }
}

public enum CalendarOutboxOperation
{
    Create = 1,
    Update = 2,
    Delete = 3,
}

/// <summary>
/// Durable queue of pending Google Calendar operations driven by the Phase 3
/// Apollo pipeline. Each row represents one CalendarEvent state change that
/// needs to be reflected in GCal: a Create (event newly parsed from Apollo),
/// an Update (Apollo edited the post), or a Delete (Apollo pulled the post
/// before EndUtc).
///
/// Written by ApolloMessageParserWorker when UseNewApolloPipeline=true.
/// Drained by CalendarOutboxWorker on its own schedule with row-level
/// exponential backoff via NextAttemptAt. Idempotent against duplicate
/// processing because Create operations check CalendarEvent.CalendarEventId
/// before calling GCal — if it's already populated, the create is folded
/// into an update.
///
/// ── Why an outbox ──
/// Pre-Phase-3, ApolloEventHandler called GoogleCalendarService synchronously
/// inside the gateway-event callback. Transient GCal failures (5xx, throttling,
/// auth refresh blip) became silent drift — the CalendarEvent row was written
/// but the API call wasn't, and the inconsistency only surfaced when
/// /cleanup-calendar-dupes ran. The outbox decouples the DB write from the
/// external call so a failure is a deferred retry, not a lost operation.
/// </summary>
public class CalendarOutbox
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public CalendarOutboxOperation Operation { get; set; }

    /// <summary>FK to CalendarEvent.Id (our internal id, not GoogleEventId).</summary>
    public int CalendarEventId { get; set; }

    /// <summary>
    /// JSON-serialized CalendarOutboxPayload. Captured at enqueue time so a
    /// later edit doesn't retroactively change what an in-flight queue row
    /// will push to GCal. For Delete operations, this also carries the
    /// Google event ID, because the CalendarEvent row is removed at enqueue
    /// time and is no longer available when the worker processes the row.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;

    public int AttemptCount { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}