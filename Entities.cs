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