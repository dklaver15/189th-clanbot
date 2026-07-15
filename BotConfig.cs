using System.Reactive;

namespace ClanGuardBot.Models;

/// <summary>
/// Configuration loaded from appsettings.json / environment variables.
/// </summary>
public class BotConfig
{
    public const string Section = "BotConfig";

    /// <summary>Discord bot token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Name of the role to assign to inactive users.</summary>
    public string AwolRoleName { get; set; } = "AWOL";

    /// <summary>
    /// Discord channel ID of the HQ/AWOL channel where AWOL notifications are
    /// posted. Takes precedence over HqChannelName when non-zero. Prefer this
    /// over the name lookup — it is immune to channel renames (e.g. adding an
    /// emoji to the channel name, which silently breaks the name match).
    /// </summary>
    public ulong HqChannelId { get; set; } = default;

    /// <summary>
    /// Name of the HQ channel where AWOL notifications are posted. Used only as
    /// a fallback when HqChannelId is unset (0) or does not resolve in the guild.
    /// </summary>
    public string HqChannelName { get; set; } = "hq";

    /// <summary>
    /// Discord user IDs that should never be left in the guild's AFK channel.
    /// When Discord's inactivity timeout moves one of these users into the AFK
    /// channel, the bot immediately moves them back to the channel they were in.
    /// Discord has no native per-user AFK exemption, so this emulates one.
    /// Note: the timeout re-fires, so the user is pulled back roughly every
    /// inactivity interval rather than being exempted invisibly.
    /// </summary>
    public List<ulong> AfkExemptUserIds { get; set; } = new();

    /// <summary>Rolling window in days for activity tracking.</summary>
    public int WindowDays { get; set; } = 28;

    /// <summary>Shorter rolling window in days for roles listed in ShortWindowRoles.</summary>
    public int ShortWindowDays { get; set; } = 14;

    /// <summary>
    /// Comma-separated list of role names that use the shorter activity window.
    /// e.g. "Guest,RCT"
    /// </summary>
    public string ShortWindowRoles { get; set; } = "Guest,RCT";

    /// <summary>Minimum messages required within the window to stay active.</summary>
    public int MinMessages { get; set; } = 5;

    /// <summary>Minimum voice hours required within the window to stay active.</summary>
    public double MinVoiceHours { get; set; } = 1.0;

    /// <summary>Days after AWOL assignment before HQ is notified.</summary>
    public int AwolGraceDays { get; set; } = 2;

    /// <summary>
    /// When true, the bot DMs a member the moment it assigns them the AWOL
    /// role, so they know they've been flagged and how to clear it. Best-effort:
    /// a closed DM or a rate-limited DM route never blocks role assignment or
    /// the check cycle. Set false to return to silent (HQ-only) AWOL handling.
    /// </summary>
    public bool AwolDmOnAssign { get; set; } = true;

    /// <summary>
    /// Upper age bound for posting an AWOL notification. A pending record whose
    /// AssignedAt is older than this is considered stale: Step 3 silently
    /// resolves it (marks it sent, logs a warning) instead of posting it.
    ///
    /// This is a blast-radius guard. Step 3 otherwise posts ANY pending record
    /// past the grace period, so any event that resets NotificationSent on
    /// historical rows — a DB restore, a stray bulk UPDATE, a backup rollback —
    /// would carpet-bomb the channel with weeks-old alerts. With this cap, such
    /// stale rows are quietly closed instead. Set to 0 to disable the guard.
    ///
    /// Default 4 = 2× the default grace period, so a normal recent AWOL (which
    /// posts within a day of crossing the grace cutoff) is never affected, while
    /// anything materially older than the legitimate notify window is suppressed.
    /// Keep this comfortably below NotificationGiveUpWindow (7d) so genuinely
    /// stale records are caught by the age guard rather than the give-up path.
    /// </summary>
    public int AwolNotificationMaxAgeDays { get; set; } = 4;

    /// <summary>
    /// When true, the bot posts a reminder in <see cref="AwolWipeReminderChannelId"/>
    /// on the <see cref="AwolWipeReminderDayOfMonth"/> of every month (at
    /// <see cref="AwolWipeReminderHourEt"/>), tagging
    /// <see cref="AwolWipeReminderMentionUserIds"/> to wipe the AWOLs
    /// (/kick-awols) and clear the list (/clear-awol-list). Set false to disable
    /// the monthly reminder entirely.
    /// </summary>
    public bool AwolWipeReminderEnabled { get; set; } = true;

    /// <summary>
    /// Channel where the monthly AWOL wipe reminder is posted. The reminder is
    /// idle if this is 0 or <see cref="AwolWipeReminderEnabled"/> is false.
    /// </summary>
    public ulong AwolWipeReminderChannelId { get; set; } = 1501365290569564220;

    /// <summary>
    /// User IDs tagged (pinged) in the monthly AWOL wipe reminder. Empty means the
    /// reminder still posts but pings no one.
    /// </summary>
    public List<ulong> AwolWipeReminderMentionUserIds { get; set; } =
        new() { 825902140639412225, 1290399637882273894 };

    /// <summary>
    /// Day of the month (1-31) on which the monthly AWOL wipe reminder posts.
    /// Clamped to the month's last day, so 31 still fires on Feb 28/29. Default 23.
    /// </summary>
    public int AwolWipeReminderDayOfMonth { get; set; } = 23;

    /// <summary>
    /// Hour of day (0-23, US Eastern / America/New_York) at which the monthly AWOL
    /// wipe reminder posts. Default 9 = 9am ET.
    /// </summary>
    public int AwolWipeReminderHourEt { get; set; } = 9;

    /// <summary>How often (in minutes) the background check runs.</summary>
    public int CheckIntervalMinutes { get; set; } = 180;

    /// <summary>
    /// Comma-separated list of role names that are exempt from AWOL tracking.
    /// Officers, admins, bots, etc.
    /// </summary>
    public string ExemptRoles { get; set; } = "Admin,Moderator,Retired,Bot,Bot Whisperer";

    /// <summary>
    /// Name of the role for clan members on Reserve status. Reserve members are
    /// fully exempt from AWOL tracking — they will never be assigned the AWOL
    /// role and will never be kicked by /kick-awols. Tracked separately from
    /// ExemptRoles so that "Reserve" carries explicit clan-doctrine meaning
    /// (active-but-paused members) rather than being lumped in with bots/admins.
    /// </summary>
    public string ReserveRoleName { get; set; } = "Reserve";

    /// <summary>Path to the Google service account credentials JSON file.</summary>
    public string GoogleCredentialsPath { get; set; } = "google-credentials.json";

    /// <summary>The Google Spreadsheet ID (from the sheet URL).</summary>
    public string GoogleSpreadsheetId { get; set; } = string.Empty;

    /// <summary>The sheet/tab name to write gamertags to.</summary>
    public string GoogleSheetName { get; set; } = "Gamertags";

    /// <summary>The sheet/tab name for the nightly roster export.</summary>
    public string RosterSheetName { get; set; } = "Roster";

    /// <summary>The Google Spreadsheet ID for the roster export (separate from gamertags). If empty, uses GoogleSpreadsheetId.</summary>
    public string RosterSpreadsheetId { get; set; } = string.Empty;

    /// <summary>Name of the voice channel to track for "Last Events" column.</summary>
    public string EventsVoiceChannelName { get; set; } = "Events";

    /// <summary>
    /// Discord channel ID of the primary events voice channel. Used as the
    /// legacy fallback for sessions recorded before CategoryId existed on
    /// VoiceSession — immune to renames. New sessions are matched by
    /// EventsCategoryId instead so any VC under the EVENTS category counts.
    /// </summary>
    public ulong EventsVoiceChannelId { get; set; } = default;

    /// <summary>
    /// Discord category ID that contains event voice channels. Voice time in
    /// ANY channel under this category (including temporary event VCs spun
    /// up for overflow, squad splits, etc.) counts toward event attendance
    /// for auto-promotion and the roster export. Used alongside
    /// EventsVoiceChannelId — sessions with a null CategoryId (recorded
    /// before this column existed) fall back to the legacy channel-ID check.
    /// Immune to category renames and to emoji changes in the category name.
    /// </summary>
    public ulong EventsCategoryId { get; set; } = default;

    /// <summary>Hour of day (UTC, 0-23) to run the nightly roster export.</summary>
    public int RosterExportHourUtc { get; set; } = 6;

    /// <summary>Rolling window in days for the roster export activity stats. Independent of AWOL window.</summary>
    public int RosterWindowDays { get; set; } = 14;

    /// <summary>
    /// Comma-separated list of rank role names in order from lowest to highest.
    /// Used to identify a user's current rank and track time-in-rank.
    /// </summary>
    public string RankRoles { get; set; } = "RCT,PVT,PFC,SPC,CPL,SGT,SSG,SFC,MSG,1SG,SGM,CSM,SMA,2ndLT,1stLT,CPT,MAJ,LTC,COL,BG,MG,LTG,GEN,GA";

    /// <summary>Name of the Discord category where ticket channels are created.</summary>
    public string TicketCategoryName { get; set; } = "TICKET CENTER";

    /// <summary>How many hours to wait before sending a ticket reminder (default: 24).</summary>
    public double TicketReminderDelayHours { get; set; } = 24.0;

    /// <summary>How many days to wait before reminding a Guest who hasn't created a ticket.</summary>
    public int GuestReminderDelayDays { get; set; } = 7;

    /// <summary>Name of the text channel where guest reminders are posted.</summary>
    public string GuestReminderChannelName { get; set; } = "general-chat";

    /// <summary>
    /// Comma-separated list of platoon role names used to detect platoon assignment.
    /// </summary>
    public string PlatoonRoles { get; set; } = "Airborne Platoon,Barbarian Platoon,Commando Platoon,Dreadnought Platoon,Executioner Platoon,Firestorm Platoon,Guardian Platoon,Havoc Platoon";

    /// <summary>Name of the rules channel where the Accept Rules button lives.</summary>
    public string RulesChannelName { get; set; } = "rules";

    /// <summary>Hours to wait after a Guest completes an onboarding step before reminding them to create a ticket.</summary>
    public double OnboardingReminderDelayHours { get; set; } = 24.0;

    /// <summary>The sheet/tab name for the recruit log.</summary>
    public string RecruitSheetName { get; set; } = "Recruit Log";

    /// <summary>The Google Spreadsheet ID for the recruit log. If empty, uses GoogleSpreadsheetId.</summary>
    public string RecruitSpreadsheetId { get; set; } = string.Empty;

    // ─── Calendar Settings ───────────────────────────────────────────

    /// <summary>
    /// Google Calendar ID for the clan calendar.
    /// For a personal Gmail calendar this is typically your Gmail address (e.g. "you@gmail.com").
    /// Find it in Google Calendar → Settings → [Calendar name] → "Calendar ID".
    /// The service account must have at least "Make changes to events" permission on this calendar.
    /// </summary>
    public string GoogleCalendarId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the text channel where Apollo posts its event embeds.
    /// The bot will listen here for new posts, edits, and deletes to keep the calendar in sync.
    /// </summary>
    public string EventsTextChannelName { get; set; } = "events";

    public ulong EventsTextChannelId { get; set; } = default;

    /// <summary>
    /// Username (or partial username) of the Apollo Discord bot.
    /// Used to identify which messages in the events channel come from Apollo.
    /// </summary>
    public string ApolloBotName { get; set; } = "Apollo";

    /// <summary>
    /// Phase 3 cutover flag for the Apollo→GCal pipeline.
    ///
    /// When false (default): ApolloEventHandler runs as the live path, parsing
    /// Apollo posts inline and pushing to GCal synchronously. ApolloMessageParserWorker
    /// writes parsed events to the ApolloEvent staging table for observability.
    ///
    /// When true: ApolloEventHandler is not registered. ApolloMessageParserWorker
    /// writes directly to CalendarEvent and enqueues CalendarOutbox rows.
    /// CalendarOutboxWorker drains the queue to GCal with retry + backoff.
    ///
    /// Flip the flag in appsettings.json and restart to cut over. No code
    /// redeploy needed for the cutover itself; rollback is the reverse.
    /// </summary>
    public bool UseNewApolloPipeline { get; set; } = false;

    /// <summary>
    /// Grace window, in seconds, that the Apollo pipeline waits after seeing an
    /// event's message deleted before acting on it — to absorb Apollo's /sort,
    /// which deletes every event message and immediately re-posts it under a
    /// new ID (Discord can't reorder messages, so sorting = delete + repost).
    ///
    /// During this window, a re-post with a matching content hash re-binds the
    /// existing CalendarEvent to the new message instead of cancel-then-recreate,
    /// so Google Calendar is never touched by a sort. If no matching re-post
    /// arrives within the window, the deletion is treated as genuine.
    ///
    /// Must comfortably exceed the parser worker's poll interval (15s) so a
    /// delete and its re-post are never split across more cycles than the
    /// window covers. Default 120s = 8 poll cycles of headroom. Values &lt;= 0
    /// fall back to the default.
    /// </summary>
    public int ApolloSortRebindGraceSeconds { get; set; } = 120;

    /// <summary>
    /// Seconds to wait after a UserLeft before finalizing a departure as
    /// voluntary "Left". Long enough to absorb gateway reordering and
    /// audit-log lag between UserLeft and the matching Kick/Ban
    /// AuditLogCreated; short enough that the weekly briefing never waits on
    /// it. Floored at 15s in code.
    /// </summary>
    public int RetentionClassificationGraceSeconds { get; set; } = 90;

    /// <summary>
    /// When false, the weekly briefing omits the Retention section. Capture,
    /// classification, and offline-gap reconciliation keep running regardless,
    /// so toggling this never creates a data gap — it only hides presentation
    /// while the prompt is being tuned.
    /// </summary>
    public bool RetentionSectionEnabled { get; set; } = true;

    /// <summary>
    /// How often the MemberRosterReconciler diffs the persisted roster against
    /// the live guild to catch departures missed while the bot was offline
    /// (Discord does not replay UserLeft). Also runs once on startup. Floored
    /// at 1 hour in code. Default 6 hours.
    /// </summary>
    public int RetentionRosterReconcileHours { get; set; } = 6;

    /// <summary>
    /// The minimum rank required to use the /comp-event command.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string CompEventMinRank { get; set; } = "CPT";

    // ──────────────────────────────────────────────────────────────────────
    //  In-house events (Apollo replacement)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum rank required to create events via /event, and to edit/cancel
    /// any event. The organizer of an event may always edit/cancel their own
    /// regardless of rank. Must match a rank name in RankRoles (case-insensitive).
    /// Default is the lowest officer rank (any officer can create events).
    /// </summary>
    public string EventCommandMinRank { get; set; } = "2ndLT";

    /// <summary>
    /// Minimum rank required to SAVE or DELETE reusable event templates via
    /// /event template save | delete. Using a template (/event template use) and
    /// listing templates follow the lower <see cref="EventCommandMinRank"/> gate,
    /// since using one only creates an ordinary event. Default "CPT" — templates
    /// are a shared, server-wide resource, so curating them is left to senior
    /// officers while any event-creator can build from them. Must match a rank
    /// name in RankRoles (case-insensitive).
    /// </summary>
    public string EventTemplateManageMinRank { get; set; } = "CPT";

    /// <summary>
    /// Channel that ClanGuard-created event embeds are posted to. 0 falls back
    /// to EventsTextChannelId. Kept separate from EventsTextChannelId on purpose:
    /// that key is the channel the Apollo capture/backfill/reconciliation
    /// pipeline watches, so the in-house event posts can target a different
    /// channel without disturbing Apollo.
    /// </summary>
    public ulong EventPostChannelId { get; set; } = default;

    /// <summary>Resolves the channel ClanGuard event posts go to (EventPostChannelId, else EventsTextChannelId).</summary>
    public ulong GetEventPostChannelId() => EventPostChannelId != 0 ? EventPostChannelId : EventsTextChannelId;

    /// <summary>Master switch for the cosmetic RSVP buttons on event posts.</summary>
    public bool EventRsvpEnabled { get; set; } = true;

    // ──────────────────────────────────────────────────────────────────────
    //  Question of the Day (/qotd)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum rank required to post a Question of the Day via /qotd. Must match
    /// a rank name in RankRoles (case-insensitive). Default "SGT". Admins / users
    /// with Manage Roles always pass regardless of rank.
    /// </summary>
    public string QotdMinRank { get; set; } = "SGT";

    /// <summary>
    /// Channel the /qotd embed is posted to. Defaults to the clan's general
    /// channel.
    /// </summary>
    public ulong QotdChannelId { get; set; } = 1421928902963494922;

    /// <summary>
    /// Master switch for the pinned "Upcoming Events" board maintained by
    /// <see cref="Services.UpcomingEventsBoardService"/>. When false the service
    /// stops creating/updating the board (an existing pinned board is left in
    /// place, just no longer refreshed).
    /// </summary>
    public bool EventBoardEnabled { get; set; } = true;

    /// <summary>
    /// When true, editing an event's start time DMs everyone who RSVP'd to that
    /// event to let them know it moved. Default true.
    /// </summary>
    public bool EventRescheduleNotifyEnabled { get; set; } = true;

    /// <summary>Default event length when the creator doesn't specify an end/duration.</summary>
    public double EventDefaultDurationHours { get; set; } = 2.0;

    /// <summary>
    /// IANA timezone used to interpret a creator's natural-language time input
    /// when they have not set their own via /timezone. Display is always
    /// per-viewer (Discord &lt;t:unix&gt; markdown); this only affects input parsing.
    /// </summary>
    public string EventDefaultTimeZone { get; set; } = "America/Chicago";

    /// <summary>Master switch for event reminder pings.</summary>
    public bool EventReminderEnabled { get; set; } = true;

    /// <summary>
    /// When true, each new event reminder deletes the previous reminder for that
    /// event (e.g. the 15-min reminder removes the 60-min one) to reduce clutter.
    /// Default true.
    /// </summary>
    public bool EventReminderReplacePrevious { get; set; } = true;

    /// <summary>
    /// CSV of minutes-before-start at which a reminder fires (e.g. "60,15").
    /// Add or remove values freely; the worker fires once per value per event
    /// and records fired values on ClanEvent.RemindersSentCsv.
    /// </summary>
    public string EventReminderLeadMinutes { get; set; } = "60,15";

    /// <summary>Channel reminders post to. 0 → fall back to EventsTextChannelId.</summary>
    public ulong EventReminderChannelId { get; set; } = default;

    /// <summary>
    /// CSV of RSVP statuses whose members get @-mentioned in the reminder
    /// (any of: Going, Maybe, Decline, Waitlisted). Default pings Going, Maybe,
    /// and the waitlist. The reminder still posts to the channel even when no one matches.
    /// </summary>
    public string EventReminderPingStatuses { get; set; } = "Going,Maybe,Waitlisted";

    /// <summary>How far ahead the recurrence scheduler materializes occurrences.</summary>
    public int EventRecurrenceHorizonDays { get; set; } = 14;

    /// <summary>
    /// Cap on how many occurrences the scheduler will create in a single pass,
    /// guarding against a flood if the bot was offline for an extended period.
    /// </summary>
    public int EventRecurrenceMaxBackfill { get; set; } = 4;

    /// <summary>
    /// When true, a ClanGuard-created event's #events post is deleted
    /// EventArchiveDelayMinutes after the event ends. The Google Calendar entry
    /// and attendance records are kept — only the Discord message is removed.
    /// Apollo events are never touched.
    /// </summary>
    public bool EventAutoArchiveEnabled { get; set; } = true;

    /// <summary>Minutes after an event's end before its #events post is auto-deleted.</summary>
    public int EventArchiveDelayMinutes { get; set; } = 60;

    /// <summary>
    /// The minimum rank required to use the /promote and /demote commands.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string PromoteDemoteMinRank { get; set; } = "2ndLT";

    /// <summary>
    /// The minimum rank required to use the /kick-awols and /clear-awol-list commands.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// Members at or above this rank are also PROTECTED from being kicked by
    /// /kick-awols, even if they somehow ended up flagged AWOL — this is the
    /// safety net against a tracking bug nuking leadership.
    /// </summary>
    public string AwolKickMinRank { get; set; } = "MAJ";

    /// <summary>
    /// The minimum rank required to use the /briefing-now command, which
    /// triggers the weekly officer briefing immediately. Defaults to "BG"
    /// so only HQ-level leadership can trigger ad-hoc briefings to HQ.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string BriefingNowMinRank { get; set; } = "BG";

    /// <summary>
    /// The minimum rank required to (a) relabel an existing tracked invite
    /// via /invite assign, and (b) revoke a tracked invite via /invite
    /// revoke (Phase 2). Both operations are sensitive enough to gate —
    /// /invite assign can rewrite labels other officers depend on, and
    /// /invite revoke kills a live link.
    ///
    /// /invite create deliberately is NOT gated by this — it's gated by
    /// Discord's native "Create Invite" channel permission so any member
    /// who can already create an invite via the Discord UI can also create
    /// a labeled one. Recruiters labeling their own personal links shouldn't
    /// have to ladder-climb.
    ///
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string InviteManagementMinRank { get; set; } = "2ndLT";

    // ─── Auto-Promotion Settings ─────────────────────────────────────

    /// <summary>
    /// Master switch for the nightly AutoPromotionService.
    /// When false, the service exits immediately on startup.
    /// </summary>
    public bool AutoPromotionEnabled { get; set; } = true;

    /// <summary>
    /// When true, AutoPromotionService logs what it WOULD do but does not apply
    /// any role changes or post announcements. Recommended for first-run verification.
    /// </summary>
    public bool AutoPromotionDryRun { get; set; } = false;

    /// <summary>
    /// Comma-separated list of "from" rank names whose tier should be dry-run
    /// even when AutoPromotionDryRun is false. Useful for rolling out a newly
    /// added tier with the rest of the ladder still running live.
    /// Example: "CPL" = the CPL→SGT tier dry-runs; RCT→CPL still promote live.
    /// Empty = no per-tier overrides.
    /// </summary>
    public string AutoPromotionDryRunRanks { get; set; } = "CPL";

    /// <summary>
    /// Hour of day (UTC, 0-23) to run the nightly auto-promotion check.
    /// </summary>
    public int AutoPromotionRunHourUtc { get; set; } = 3;

    /// <summary>
    /// Name of the text channel where auto-promotion announcements are posted.
    /// </summary>
    public string AutoPromotionAnnouncementChannel { get; set; } = "general-chat";

    public ulong AutoPromotionAnnouncementChannelId { get; set; }

    /// <summary>
    /// Seconds to wait between successive promotion announcement posts. Prevents
    /// burst-posting when many members are promoted in a single run (especially
    /// the first live run after a backlog). Discord's channel rate limit is
    /// roughly 5 messages per 5 seconds, so 5 seconds is a safe floor. Set to 0
    /// to disable throttling entirely.
    /// </summary>
    public int AutoPromotionAnnouncementDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Grace period (minutes) on each side of a scheduled event when counting
    /// voice attendance. 30 = user can show up 30 min early or leave 30 min
    /// late and still get attendance credit. Used by
    /// EventAttendanceSnapshotService when computing per-event attendance.
    /// </summary>
    public int AutoPromotionEventBufferMinutes { get; set; } = 30;

    /// <summary>
    /// Minimum cumulative minutes a user must be in the events VC (during the
    /// buffered event window) to count as having attended that event.
    /// </summary>
    public int AutoPromotionMinEventAttendanceMinutes { get; set; } = 30;

    /// <summary>
    /// How often (minutes) the EventAttendanceSnapshotService wakes up to check
    /// for newly-ended events that need attendance rows written. Shorter intervals
    /// reduce the window of attendance loss if a CalendarEvent is deleted before
    /// we snapshot it; longer intervals reduce DB load. 5 minutes is a good
    /// default for a clan with a few events per week. Minimum 1.
    /// </summary>
    public int EventAttendanceSnapshotIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Comma-separated list of CalendarEvent.Source values that count toward
    /// auto-promotion attendance. CompDiv events (comp team scrims) don't count
    /// toward clan-wide promotion points and are excluded by default. If new
    /// source types are added in the future, add them here explicitly to opt
    /// them in. Case-insensitive.
    /// </summary>
    public string AttendanceCountingSources { get; set; } = "Clan";

    // ─── Meeting Attendance Settings ─────────────────────────────────

    /// <summary>
    /// Discord channel ID of the dedicated meetings voice channel. Voice
    /// time in this VC during the buffered window of a clan-source Apollo
    /// event counts as meeting attendance (which, in turn, counts as event
    /// attendance for promotion math — see MeetingAttendanceSnapshotService
    /// and EventAttendanceHelper).
    ///
    /// Unlike EventsCategoryId, this is a single channel — meetings happen
    /// in one specific room rather than spreading across a category of
    /// per-squad VCs. Leave at 0 to disable meeting attendance entirely;
    /// MeetingAttendanceSnapshotService is a no-op when this is unset.
    /// Immune to channel renames.
    /// </summary>
    public ulong MeetingVoiceChannelId { get; set; } = default;

    /// <summary>
    /// Minimum cumulative minutes a user must be in the meetings VC (during
    /// the buffered Apollo-event window) to count as having attended that
    /// meeting. Defaults to 15 — shorter than the 30-min event threshold
    /// since meetings are typically shorter, more dialog-heavy, and easier
    /// to fully attend than a full clan event.
    ///
    /// The buffer (AutoPromotionEventBufferMinutes) and snapshot cadence
    /// (EventAttendanceSnapshotIntervalMinutes) are shared with event
    /// attendance — meetings ride the same pipeline timing.
    /// </summary>
    public int AutoPromotionMinMeetingAttendanceMinutes { get; set; } = 15;

    // ─── Meeting Recording Settings ──────────────────────────────────

    /// <summary>
    /// Master switch for the meeting-recording pipeline. When false,
    /// MeetingRecordingScheduler logs a one-line "disabled" notice and exits
    /// without consuming any DB or Discord cycles. Independent of meeting
    /// *attendance* (MeetingVoiceChannelId), which keeps working regardless —
    /// recording is a separate, opt-in concern.
    /// </summary>
    public bool MeetingRecordingEnabled { get; set; } = false;

    /// <summary>
    /// OPTIONAL. Recording is now triggered purely by voice-channel presence
    /// (see <see cref="MeetingRecordingMinPresenceToStart"/>) — a calendar event
    /// is no longer required to record. This pattern is only used to *name* a
    /// presence-triggered recording: when a clan-source Apollo event happens to
    /// overlap the moment recording starts, its title is borrowed for the minutes.
    /// If set, a concurrent event whose Title matches (case-insensitively) is
    /// preferred; if empty, any concurrent clan event is used, and if there is no
    /// concurrent event at all the recording is named after the voice channel and
    /// date. Never gates the feature.
    /// </summary>
    public string MeetingTitlePattern { get; set; } = "monthly meeting";

    /// <summary>
    /// How many non-bot members must be in the meeting voice channel before the
    /// recorder joins and starts capturing. Default 2, so one person sitting
    /// alone in the channel is never recorded — recording begins once a real
    /// conversation (two or more people) forms. Set to 1 to record the instant
    /// anyone joins.
    /// </summary>
    public int MeetingRecordingMinPresenceToStart { get; set; } = 2;

    /// <summary>
    /// Retained for backward compatibility. With presence-driven recording the
    /// recorder joins when people are already in the channel, so there is no
    /// pre-meeting lead to honour; this value is now only used as the +/- buffer
    /// when looking for a concurrent event to borrow a title from.
    /// </summary>
    public int MeetingRecordingLeadMinutes { get; set; } = 5;

    /// <summary>
    /// MeetingRecordingScheduler poll cadence in minutes. Each poll re-reads
    /// the matched event (catching reschedules/renames), reconciles
    /// cancellations, and drives the recording state machine. Default 2.
    /// </summary>
    public int MeetingRecordingPollIntervalMinutes { get; set; } = 2;

    /// <summary>
    /// How many of the most recent meetings' *audio* recordings to retain.
    /// Older audio is deleted by the retention sweep; the lightweight
    /// transcript + minutes are kept regardless, so meeting history survives
    /// past the audio. Default 3.
    /// </summary>
    public int MeetingRecordingRetainCount { get; set; } = 3;

    /// <summary>
    /// Channel ID for the recording-consent notice (and, later, the posted
    /// minutes). 0 falls back to the meeting voice channel's own text chat
    /// (voice channels are message channels in modern Discord).
    /// </summary>
    public ulong MeetingRecordingAnnouncementChannelId { get; set; } = default;

    /// <summary>
    /// Base URL of the @discordjs/voice recorder sidecar (e.g.
    /// "http://recorder:8080" on the compose network). When empty, the
    /// recorder is considered not deployed: Program.cs registers the no-op
    /// LoggingMeetingRecorderController, the scheduler still runs, but no audio
    /// is captured. Setting this activates the real HTTP recorder controller.
    /// </summary>
    public string MeetingRecorderBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret sent as the x-recorder-secret header on every call to the
    /// recorder sidecar. Must match the sidecar's RECORDER_SHARED_SECRET.
    /// Inject via env (BotConfig__MeetingRecorderSharedSecret) — never commit it.
    /// </summary>
    public string MeetingRecorderSharedSecret { get; set; } = string.Empty;

    // ─── Meeting Minutes / Transcription ─────────────────────────────

    /// <summary>
    /// Base URL of the faster-whisper transcriber sidecar (e.g.
    /// "http://transcriber:8090" on the compose network). Empty disables the
    /// whole minutes pipeline: MeetingMinutesService becomes a no-op and
    /// recordings accumulate in the Transcribing state until it's configured.
    /// </summary>
    public string MeetingTranscriberBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret sent as the x-transcriber-secret header to the transcriber
    /// sidecar. Must match the sidecar's TRANSCRIBER_SHARED_SECRET. Inject via
    /// env (BotConfig__MeetingTranscriberSharedSecret) — never commit it.
    /// </summary>
    public string MeetingTranscriberSharedSecret { get; set; } = string.Empty;

    /// <summary>
    /// HTTP timeout (minutes) for a single /transcribe call. With per-speaker
    /// continuous tracks an hour-long meeting transcribes in a few minutes, but
    /// this runs on a background worker with no user waiting, so the default is
    /// generous to absorb a very long meeting or a slower model. Default 90.
    /// </summary>
    public int MeetingTranscriberTimeoutMinutes { get; set; } = 90;

    /// <summary>
    /// Filesystem path where the recorder sidecar writes meeting audio
    /// directories (meeting_{id}_*), as seen from the BOT container. This is the
    /// shared meeting-audio volume mount — the same volume the recorder writes
    /// to — so the scheduler can reconcile a finalized recording from its
    /// manifest.json if the recorder lost its in-memory state (e.g. a restart),
    /// instead of wrongly marking a meeting Failed when its audio is on disk.
    /// Defaults to the standard compose mount. Empty disables disk reconciliation.
    /// </summary>
    public string MeetingRecordingsPath { get; set; } = "/app/data/recordings";

    /// <summary>
    /// Channel ID where generated minutes are posted. 0 falls back to
    /// MeetingRecordingAnnouncementChannelId, then to the meeting VC's text chat.
    /// </summary>
    public ulong MeetingMinutesChannelId { get; set; } = default;

    /// <summary>
    /// Transcript size (characters) above which minutes generation switches from a
    /// single Claude call to a map-reduce over chunks. A normal monthly meeting is
    /// well under this and takes the single-shot path unchanged; only an unusually
    /// long meeting is chunked, so its transcript can never overflow the model's
    /// context window. Lower it to exercise the chunked path in testing. Default 200000.
    /// </summary>
    public int MeetingMinutesMaxSingleShotChars { get; set; } = 200_000;

    /// <summary>
    /// Maximum characters of transcript per chunk when the map-reduce path is used
    /// (see <see cref="MeetingMinutesMaxSingleShotChars"/>). Each chunk is condensed
    /// to notes by one Claude call, then all notes are reduced into the final
    /// minutes. Kept well inside the context window. Default 150000.
    /// </summary>
    public int MeetingMinutesChunkChars { get; set; } = 150_000;

    // ─── Voice Activity Safeguards ───────────────────────────────────

    /// <summary>
    /// Cap on how many hours a single voice session can contribute to activity
    /// counts (AWOL checks + auto-promotion). Protects against stuck sessions
    /// that were never properly closed (e.g. because the bot crashed while users
    /// were in voice). 12 hours is generous enough for legitimate gaming
    /// marathons while preventing impossibly-long orphaned sessions from inflating
    /// totals. Set to 0 or negative to disable the cap (not recommended).
    /// </summary>
    public double MaxSingleSessionHours { get; set; } = 12.0;

    // ─── Bump Reminder Settings ──────────────────────────────────────

    /// <summary>
    /// Master switch for the BumpReminderHandler. When false, the handler
    /// ignores Disboard messages and skips startup recovery.
    /// </summary>
    public bool BumpReminderEnabled { get; set; } = true;

    /// <summary>
    /// How long to wait after the last observed Disboard bump before reminding
    /// the channel. Disboard's actual cooldown is 2h; the default 2.5h is a
    /// soft buffer so a member who notices the timer just past 2h has time to
    /// bump before the bot fires an unnecessary reminder.
    /// </summary>
    public double BumpReminderDelayHours { get; set; } = 2.5;

    /// <summary>
    /// Discord channel ID where bump reminders are posted. Defaults to the
    /// general-chat channel that auto-promotion announcements use.
    /// </summary>
    public ulong BumpReminderChannelId { get; set; } = 1421928902963494922;

    /// <summary>
    /// Discord user ID of the Disboard bot. The standard public Disboard bot
    /// is 302050872383242240 — only override this if Disboard ever migrates
    /// to a new bot account.
    /// </summary>
    public ulong DisboardBotId { get; set; } = 302050872383242240;

    /// <summary>
    /// On bot restart, if a scheduled reminder was missed by less than this
    /// many minutes, fire it immediately. Anything older is considered stale
    /// (someone may have bumped during downtime) and the reminder is shifted
    /// forward by a full BumpReminderDelayHours window instead.
    /// </summary>
    public int BumpReminderRestartGraceMinutes { get; set; } = 30;

    // ─── Discord Status Monitor Settings ─────────────────────────────

    /// <summary>
    /// Master switch for the DiscordStatusMonitorService. When false the
    /// service exits at startup and the polling loop never runs.
    /// </summary>
    public bool DiscordStatusMonitorEnabled { get; set; } = true;

    /// <summary>
    /// Discord channel ID where Discord-status incident updates are
    /// posted. The bot needs "Send Messages" plus "Mention Everyone"
    /// in this channel for the @here ping to actually fire — without
    /// Mention Everyone the message still sends, just without the ping.
    /// </summary>
    public ulong DiscordStatusChannelId { get; set; } = 1377427599415971891;

    /// <summary>
    /// How often (in minutes) to poll discordstatus.com. The Statuspage
    /// JSON is cached aggressively at the CDN, so values below 2 don't
    /// buy fresher data. Default 2.
    /// </summary>
    public int DiscordStatusPollIntervalMinutes { get; set; } = 2;

    /// <summary>
    /// Mention string prefixed to incident posts. Default <c>@here</c>.
    /// Set to empty string to post silently (embed still goes out).
    /// Only <c>@here</c> and <c>@everyone</c> are wired up to fire as
    /// pings — a role mention like <c>&lt;@&amp;ROLE_ID&gt;</c> would post
    /// as plain text without pinging unless the handler is extended to
    /// set AllowedMentions.RoleIds. Same constraint as
    /// <see cref="AuditLogWatcherCriticalMention"/>.
    /// </summary>
    public string DiscordStatusMention { get; set; } = "@here";

    // ─── Officer Application Settings ────────────────────────────────

    /// <summary>
    /// Discord channel ID of the member-facing instructions channel that hosts
    /// the persistent "Apply for Officer" button. Posted to by
    /// /setup-officer-app. Must be a channel everyone eligible to apply can
    /// see — typically a read-only general announcements / info channel.
    /// </summary>
    public ulong OfficerAppInstructionsChannelId { get; set; } = default;

    /// <summary>
    /// Discord channel ID of the HQ-only channel that receives dossier embeds
    /// when applications are submitted (Phase 2). The bot must have Send
    /// Messages + Embed Links permission here, plus the ability to mention
    /// the configured HQ role.
    /// </summary>
    public ulong OfficerAppHqChannelId { get; set; } = default;

    /// <summary>
    /// Discord role ID of the HQ role. Used (a) to gate the /setup-officer-app
    /// slash command, and (b) to ping HQ when an application is submitted to
    /// the dossier channel (Phase 2).
    /// </summary>
    public ulong OfficerAppHqRoleId { get; set; } = default;

    /// <summary>
    /// Minimum rank a member must hold to submit an officer application.
    /// Must match an entry in RankRoles (case-insensitive). Members below
    /// this rank will be shown an ephemeral "not eligible" message when they
    /// press the apply button (Phase 2 enforces).
    /// </summary>
    public string OfficerAppMinimumRank { get; set; } = "SGT";

    /// <summary>
    /// Optional URL of a small thumbnail image (e.g. the 189th logo) shown in
    /// the upper-right of the instructions embed. Must be a public HTTPS URL
    /// Discord can fetch — the easiest source is to upload the image to any
    /// Discord channel, right-click and "Copy Image Link" to get the
    /// cdn.discordapp.com URL. Empty disables the thumbnail.
    /// </summary>
    public string OfficerAppInstructionsThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional URL of a large banner image shown at the bottom of the
    /// instructions embed. Same hosting requirement as the thumbnail. Empty
    /// disables the banner.
    /// </summary>
    public string OfficerAppInstructionsBannerImageUrl { get; set; } = string.Empty;

    // ─── Ticket System ───────────────────────────────────────────────

    /// <summary>
    /// Public tickets channel. Hosts the persistent ticket panel (category
    /// select menu, posted by /ticket-panel) AND is the parent channel under
    /// which per-ticket private threads are created. Must be visible to every
    /// member who should be able to open a ticket — per-ticket privacy comes
    /// from the private threads, not this channel. Leave 0 to disable the
    /// ticket system.
    /// </summary>
    public ulong TicketCenterChannelId { get; set; } = default;

    /// <summary>
    /// HQ-only log channel that receives a transcript when a ticket is closed.
    /// The bot needs Send Messages + Embed Links + Attach Files here. Leave 0
    /// to skip transcript posting (tickets still close; nothing is logged).
    /// </summary>
    public ulong TicketLogChannelId { get; set; } = default;

    /// <summary>
    /// Fallback HQ role for tickets whose category defines no routed role
    /// (RoutedRoleId = 0). Also gates /ticket-panel and the claim/close/priority
    /// controls (Administrators always pass). Typically the same value as
    /// OfficerAppHqRoleId. Leave 0 to restrict controls to Administrators only.
    /// </summary>
    public ulong TicketHqRoleId { get; set; } = default;

    /// <summary>
    /// Role permitted to unmask an anonymous reporter (a later phase). Kept
    /// separate from TicketHqRoleId so the ability to see who filed an
    /// anonymous report can be delegated to a narrower group. Leave 0 to
    /// restrict unmasking to Administrators only.
    /// </summary>
    public ulong TicketUnmaskRoleId { get; set; } = default;

    /// <summary>
    /// Role the escalation sweep pings when a ticket passes the second
    /// escalation threshold (a later phase). Leave 0 to escalate to the
    /// routed role again instead of a distinct higher role.
    /// </summary>
    public ulong TicketEscalationRoleId { get; set; } = default;

    /// <summary>
    /// Hours a ticket may sit without a staff reply before the escalation
    /// sweep bumps its priority and re-pings the routed role. 0 disables the
    /// first escalation.
    /// </summary>
    public double TicketFirstEscalationHours { get; set; } = 24.0;

    /// <summary>
    /// Hours a ticket may sit before the sweep escalates it to
    /// TicketEscalationRoleId. 0 disables the second escalation.
    /// </summary>
    public double TicketSecondEscalationHours { get; set; } = 72.0;

    /// <summary>
    /// Days of inactivity after which an open ticket is auto-closed by
    /// TicketMaintenanceService. Until this threshold the sweep also keeps the
    /// thread un-archived, which is how a ticket stays visible past Discord's
    /// hard 1-week auto-archive cap (Discord has no native 2-week option).
    /// 0 disables both the keep-alive and the auto-close.
    /// </summary>
    public int TicketAutoCloseInactivityDays { get; set; } = 14;

    /// <summary>
    /// How often (minutes) TicketMaintenanceService runs its keep-alive +
    /// auto-close sweep. Hourly is plenty — the window is measured in days.
    /// </summary>
    public int TicketMaintenanceIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Optional small thumbnail (e.g. the 189th logo) for the ticket panel and
    /// ticket embeds. Public HTTPS URL Discord can fetch. Empty disables it.
    /// </summary>
    public string TicketPanelThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// Category key (must match a key in TicketCategoriesCsv) that gets the
    /// time-off approval flow: an "Approve Leave" control that assigns the
    /// Reserve role (AWOL-exempt) for a date range and auto-removes it when the
    /// window ends. Empty disables the time-off flow entirely.
    /// </summary>
    public string TicketTimeOffCategoryKey { get; set; } = "awol";

    /// <summary>
    /// Maximum length (in days, inclusive) of a single approved time-off window.
    /// A safety cap so a fat-fingered end date (e.g. 2099) can't hold the Reserve
    /// role indefinitely. Approvals longer than this are rejected. 0 disables the
    /// cap.
    /// </summary>
    public int TicketMaxLeaveDays { get; set; } = 180;

    /// <summary>
    /// Ticket category definitions, one per entry, entries separated by ';'
    /// and fields within an entry by '|':
    ///
    ///   key|Label|emoji|routedRoleId|anonymousAllowed|defaultPriority
    ///
    ///   • key              — stable lowercase id used in custom IDs + DB
    ///   • Label            — text shown in the select menu + embeds
    ///   • emoji            — unicode emoji for the select option ("" for none)
    ///   • routedRoleId     — role pinged/added on open; 0 falls back to TicketHqRoleId
    ///   • anonymousAllowed — true|false; true hides the opener's identity
    ///   • defaultPriority  — Low|Normal|High|Urgent
    ///
    /// Parsed by GetTicketCategories(). Malformed entries are skipped.
    /// </summary>
    public string TicketCategoriesCsv { get; set; } =
        "awol|AWOL / Time-off|⏱️|0|false|Normal;"
      + "recruit|Recruitment|🎯|0|false|Normal;"
      + "report|Report a Member|🚨|0|true|High;"
      + "tech|Bot / Tech Issue|🛠️|0|false|Normal;"
      + "portal|Battlefield Portal|🌀|1523406872655167640|false|Normal;"
      + "suggest|Suggestion / Feedback|💡|0|false|Low;"
      + "general|General|📩|0|false|Normal";

    // ─── Gamertag Button Settings ────────────────────────────────────

    /// <summary>
    /// Discord channel ID of the member-facing instructions channel that hosts
    /// the persistent "Enter Gamertags" button. Posted to by
    /// /setup-gamertags. Should be a channel everyone in the clan can see —
    /// typically a welcome / info channel so new members find it during
    /// onboarding.
    /// </summary>
    public ulong GamertagInstructionsChannelId { get; set; } = default;

    /// <summary>
    /// Discord role ID gating /setup-gamertags. Administrators always pass;
    /// otherwise the invoker must hold this role. Leave at 0 to restrict the
    /// command to Administrators only. Typically set to the HQ role (often the
    /// same role as OfficerAppHqRoleId, but kept separate so the two setup
    /// commands can be delegated independently if needed).
    /// </summary>
    public ulong GamertagSetupRoleId { get; set; } = default;

    /// <summary>
    /// Public URL of the master roster spreadsheet, shown at the bottom of the
    /// gamertags instructions message so members can browse everyone's handles.
    /// Typically a Google Sheets "Publish to web" link (the /d/e/2PACX-.../pubhtml
    /// form) — Discord auto-unfurls it into a preview card. Leave empty to
    /// suppress the roster section entirely; the rest of the message still posts.
    /// </summary>
    public string GamertagRosterUrl { get; set; } = string.Empty;

    // ─── THE FINALS leaderboard integration ─────────────────────────
    /// <summary>
    /// Master switch for the THE FINALS leaderboard feature (the /finals-rank
    /// command, the clan leaderboard board, and rank-up announcements). The
    /// community API needs no key, so this is the only gate that must be flipped
    /// to turn the feature on. Default false — opt-in.
    /// </summary>
    public bool FinalsEnabled { get; set; } = false;

    /// <summary>
    /// Leaderboard version (season) id queried on the community API, e.g. "s10"
    /// for Season 10. With <see cref="FinalsAutoDetectSeason"/> on (the default)
    /// this is just the SEED/fallback — the bot resolves the live season itself
    /// and this only matters before the first resolve or if auto-detect is off.
    /// With auto-detect off, this is authoritative and must be bumped each season.
    /// Non-season ids ("cb1", "ob") are also accepted. Default "s10".
    /// </summary>
    public string FinalsLeaderboardVersion { get; set; } = "s10";

    /// <summary>
    /// When true (default), the bot determines the current live season on its own
    /// at each refresh — the highest "sN" leaderboard that still returns data — and
    /// uses it instead of the static <see cref="FinalsLeaderboardVersion"/> (which
    /// becomes just a starting point). This means a season rollover is picked up
    /// automatically with no config change or restart; an FYI is still posted (see
    /// <see cref="FinalsSeasonRolloverReminderEnabled"/>). Set false to pin the
    /// season to <see cref="FinalsLeaderboardVersion"/> exactly. Only "sN" seeds can
    /// be auto-resolved; a non-season seed (cb1/ob) disables auto-detect implicitly.
    /// </summary>
    public bool FinalsAutoDetectSeason { get; set; } = true;

    /// <summary>
    /// Platform leaderboard to read: "crossplay" (default, recommended — covers
    /// every platform in one board), or "steam" / "psn" / "xbox" for a single
    /// platform. Matching uses whichever platform handles a member has linked
    /// regardless of this value; this only chooses which leaderboard is fetched.
    /// </summary>
    public string FinalsPlatform { get; set; } = "crossplay";

    /// <summary>
    /// How often (minutes) the background service re-fetches the leaderboard to
    /// update the board and check for rank-ups. The whole leaderboard is one
    /// fetch shared by both surfaces. Floored at 15 in code. Default 60.
    /// </summary>
    public int FinalsRefreshIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Master switch for the auto-updating clan leaderboard board. Requires
    /// <see cref="FinalsBoardChannelId"/>. Default true (no-op until the channel
    /// is set and FinalsEnabled is on).
    /// </summary>
    public bool FinalsBoardEnabled { get; set; } = true;

    /// <summary>
    /// Channel ID the clan leaderboard board is maintained in. 0 disables the
    /// board even when FinalsBoardEnabled is true.
    /// </summary>
    public ulong FinalsBoardChannelId { get; set; } = default;

    /// <summary>
    /// Master switch for rank-up announcements. Requires
    /// <see cref="FinalsAnnounceChannelId"/>. Default true (no-op until the
    /// channel is set and FinalsEnabled is on).
    /// </summary>
    public bool FinalsRankUpEnabled { get; set; } = true;

    /// <summary>
    /// Channel ID where rank-up announcements are posted. 0 disables announcements
    /// even when FinalsRankUpEnabled is true.
    /// </summary>
    public ulong FinalsAnnounceChannelId { get; set; } = default;

    /// <summary>
    /// When true, the background service watches for a new season going live (the
    /// next "sN+1" leaderboard returning data) and posts a one-time officer reminder
    /// to bump <see cref="FinalsLeaderboardVersion"/>, so the feature doesn't keep
    /// reading a frozen, archived season after a rollover. The reminder posts to
    /// <see cref="HqChannelId"/> if set, else FinalsAnnounceChannelId, else
    /// FinalsBoardChannelId. De-duped in memory per detected season. Default true.
    /// </summary>
    public bool FinalsSeasonRolloverReminderEnabled { get; set; } = true;

    // ─── Server protection: Account-Age Gate ─────────────────────────
    /// <summary>
    /// Operating mode for the account-age gate on UserJoined. One of:
    ///   "Off"        — feature disabled.
    ///   "AlertOnly"  — post a security alert for any joiner whose account
    ///                  is younger than AccountAgeGateMinDays, but take no
    ///                  action. Default — non-destructive, observation-friendly.
    ///   "Ban"        — DM the joiner, ban them, post the alert with the
    ///                  ban outcome. "Kick" is accepted as a legacy alias
    ///                  for this mode (the action is a ban either way).
    /// Comparison is case-insensitive; an unrecognized value falls back to
    /// AlertOnly with a warning log so a typo never silently enforces.
    /// </summary>
    public string AccountAgeGateMode { get; set; } = "AlertOnly";

    /// <summary>
    /// Minimum Discord account age (in days) required to remain in the
    /// server when the gate is active. Joiners with younger accounts trip
    /// the gate according to AccountAgeGateMode. Default 3 — tight enough
    /// to filter throwaway raid alts that get spun up minutes before a
    /// brigade, loose enough that legitimate new Discord users aren't
    /// blocked. Negative values are clamped to 0 (which effectively
    /// disables the age check while leaving the mode scaffolding in place).
    /// </summary>
    public int AccountAgeGateMinDays { get; set; } = 3;

    /// <summary>
    /// Channel ID where server-protection alerts (account-age gate, future
    /// raid-shield / impersonation / webhook-audit features) are posted.
    /// Default is the 189th's existing #alerts channel that the rest of
    /// the bot fleet posts to. When set to 0 the bot falls back to a
    /// name-based lookup against HqChannelName, so a fresh deploy still
    /// gets alerts somewhere visible. Prefer an explicit ID — name
    /// lookups silently break if the channel is renamed.
    /// </summary>
    public ulong SecurityAlertsChannelId { get; set; } = 1407959078105514046;

    // ─── Server protection: Invite-Link Filter ───────────────────────
    /// <summary>
    /// Operating mode for the invite-link filter on MessageReceived /
    /// MessageUpdated. One of:
    ///   "Off"        — feature disabled.
    ///   "AlertOnly"  — post a security alert + write an audit row for any
    ///                  external invite link posted by a non-exempt member,
    ///                  but DO NOT delete the message or DM the poster.
    ///                  Useful for tuning the exempt-rank floor before
    ///                  flipping to Enforce.
    ///   "Enforce"    — delete the message, best-effort DM the poster,
    ///                  post the alert, write the audit row. Default.
    /// Comparison is case-insensitive; an unrecognized value falls back to
    /// AlertOnly with a warning log so a typo never silently enforces.
    /// </summary>
    public string InviteLinkFilterMode { get; set; } = "Enforce";

    /// <summary>
    /// Members holding any rank at or above this name (in the RankRoles
    /// list) bypass the invite-link filter — they can legitimately share
    /// invites to partnered servers, sister clans, or events without
    /// being deleted. Default "MAJ" — field-grade officers and above.
    /// Server Administrators are ALSO always exempt regardless of this
    /// setting (matches every other rank-gated check in the codebase).
    /// Empty / unknown rank name disables the rank exemption (Admins
    /// still bypass).
    /// </summary>
    public string InviteLinkFilterExemptMinRank { get; set; } = "MAJ";

    // ─── Server protection: Audit Log Watcher ────────────────────────
    /// <summary>
    /// Operating mode for the audit-log watcher on Discord's
    /// <c>AuditLogCreated</c> gateway event. One of:
    ///   "Off"        — feature disabled.
    ///   "AlertOnly"  — default. Post a security alert + write an audit
    ///                  row for every watched action. No auto-response.
    /// Auto-response modes (role strip / channel lock / kick) are
    /// intentionally out of scope for v1; build trust in what the
    /// watcher catches before flipping any switch that could lock out
    /// an innocent officer on a false positive.
    /// </summary>
    public string AuditLogWatcherMode { get; set; } = "AlertOnly";

    /// <summary>
    /// Mention string prefixed to alerts for actions on the
    /// <c>CriticalActions</c> list (channel deletes, bot adds, guild
    /// settings changes). The alert embed posts unconditionally;
    /// this controls whether the embed is also ping-prefixed to wake
    /// someone up. Default <c>@here</c>. Set to empty string to
    /// disable pinging entirely (alerts still post, just silently).
    /// Note: only <c>@here</c> and <c>@everyone</c> are wired up for
    /// the AllowedMentions flag today — a role mention like
    /// <c>&lt;@&amp;ROLE_ID&gt;</c> in this field would post as plain text
    /// without pinging unless we extend the handler to set
    /// AllowedMentions.RoleIds.
    /// </summary>
    public string AuditLogWatcherCriticalMention { get; set; } = "@here";

    /// <summary>
    /// When <c>true</c> (the default) the watcher suppresses
    /// <see cref="Discord.ActionType.ChannelDeleted"/> alerts when both:
    ///   • the actor is a bot account
    ///     (<see cref="Discord.IUser.IsBot"/>), AND
    ///   • the deleted channel was a voice channel
    ///     (<see cref="Discord.ChannelType.Voice"/>).
    ///
    /// This filters out the routine churn from temporary-voice-channel
    /// bots (MEE6's temp voice, JoinToCreate, etc.) that create a
    /// channel on lobby join and tear it down on empty. Without this
    /// filter the security-alerts channel gets flooded with @here
    /// pings for activity that has no security relevance.
    ///
    /// Humans deleting voice channels still alert (officers are not
    /// bots). Bots deleting non-voice channels (text, category, forum,
    /// stage) still alert — that's rare and worth a look. ClanGuard's
    /// own actions are already filtered upstream by the
    /// <c>entry.User.Id == _client.CurrentUser.Id</c> check, so this
    /// flag only affects third-party bots.
    /// </summary>
    public bool AuditLogWatcherIgnoreBotVoiceChannelDeletes { get; set; } = true;

    // ─── Server protection: Nickname Impersonation Check ─────────────
    /// <summary>
    /// Operating mode for the nickname-impersonation watcher. Fires on
    /// <c>UserJoined</c> and <c>GuildMemberUpdated</c> (when the
    /// display name changed) and compares the suspect's display name
    /// against every CPT+ officer in the guild. One of:
    ///   "Off"        — feature disabled.
    ///   "AlertOnly"  — default. Post an alert + write an audit row
    ///                  for every match. No automatic action.
    /// Auto-revert mode (rename suspect back to plain username) is
    /// deliberately not implemented for v1 — false positives on a
    /// legitimate recruit sharing a last name with an officer would
    /// be unfair, and we want eyeballs on the alert pattern first.
    /// </summary>
    public string NicknameImpersonationMode { get; set; } = "AlertOnly";

    /// <summary>
    /// Minimum rank a member must hold to be treated as a protected
    /// officer for the impersonation check. Must match an entry in
    /// <see cref="RankRoles"/> (case-insensitive). Default <c>CPT</c>
    /// — below that, ranks are common enough that fuzzy matching
    /// false-positives heavily, and the social authority gradient
    /// drops off (a fake CPL is much less effective at running scams
    /// than a fake MAJ).
    /// </summary>
    public string NicknameImpersonationMinRank { get; set; } = "CPT";

    // ─── Server protection: Webhook Audit ────────────────────────────
    /// <summary>
    /// Operating mode for the webhook-audit periodic scan. One of:
    ///   "Off"        — feature disabled (scan service still runs but
    ///                  no work is done; the slash command also refuses).
    ///   "AlertOnly"  — default. Every 6 hours, diff the live webhook
    ///                  list against the stored snapshot and post alerts
    ///                  for new / deleted / changed webhooks.
    /// Auto-delete mode is intentionally not implemented for v1 — the
    /// false-positive cost of nuking a legitimate just-installed
    /// integration is high, and the audit-log watcher already catches
    /// brand-new webhooks in real time.
    /// </summary>
    public string WebhookAuditMode { get; set; } = "AlertOnly";

    /// <summary>
    /// How often the periodic scan runs, in minutes. Default 360
    /// (6 hours). The Audit Log Watcher (feature #3) is the real-time
    /// line of defense; this is the "did we miss anything?" sweep, so
    /// a low frequency keeps the API-call cost modest and is the right
    /// posture even on bigger servers. Clamped to a minimum of 15
    /// internally to prevent footgun.
    /// </summary>
    public int WebhookAuditScanIntervalMinutes { get; set; } = 360;

    /// <summary>
    /// Comma-separated allowlist for webhooks that should NOT trigger
    /// new-webhook or change alerts. Each entry can be either:
    ///   • A numeric application ID — matched against
    ///     <see cref="Models.WebhookSnapshot.ApplicationId"/>. Most
    ///     robust because Discord issues app IDs and they can't be
    ///     spoofed by renaming.
    ///   • A non-numeric string — matched case-insensitively against
    ///     <see cref="Models.WebhookSnapshot.Name"/>. Easier to add
    ///     ("Apollo"), but spoofable — an attacker can name their
    ///     malicious webhook "Apollo" too. Prefer the app ID when you
    ///     can look it up.
    /// Empty by default; populate as legitimate integrations get added
    /// to the server (the new-webhook alert tells you the app ID to
    /// allowlist).
    /// </summary>
    public string WebhookAuditAllowlist { get; set; } = "";

    public List<string> GetWebhookAuditAllowlist() =>
        WebhookAuditAllowlist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ─── Server protection: Token-Grabber Link Scanner ───────────────
    /// <summary>
    /// Operating mode for the anti-token-grabber link scanner. Scans
    /// every user-authored message (and edit) for URLs against
    /// (officer blocklist + Sinking Yachts phishing feed + IP-URL
    /// static pattern). One of:
    ///   "Off"        — feature disabled (the phishing-feed refresh
    ///                  service also skips fetching to save the API call).
    ///   "AlertOnly"  — default. Post to #alerts and write an audit
    ///                  row. Message is NOT deleted, no DM sent.
    /// Enforce mode (auto-delete + DM the poster) is deliberately not
    /// implemented for v1 — too easy for a brief false-positive on a
    /// popular domain to nuke a legitimate officer's message with no
    /// clear recourse. AlertOnly first builds trust in the signal.
    /// </summary>
    public string TokenGrabberScannerMode { get; set; } = "AlertOnly";

    /// <summary>
    /// Minimum rank for exemption from the scanner. Members at this
    /// rank or above can post any URL without tripping the scanner.
    /// Must match an entry in <see cref="RankRoles"/> (case-insensitive).
    /// Default <c>BG</c> — general-grade leadership doesn't need
    /// link policing, and false positives on them would be more
    /// disruptive than missing the rare BG+ phish. Server
    /// Administrators are always exempt regardless of this setting.
    /// </summary>
    public string TokenGrabberScannerExemptMinRank { get; set; } = "BG";

    /// <summary>
    /// Comma-separated officer-managed blocklist of phishing /
    /// malicious domains. Matched alongside (and prioritized over) the
    /// Sinking Yachts feed. Empty by default — populate as officers
    /// confirm specific domains from real attempted attacks. Entries
    /// match both the exact host and any subdomain (so
    /// <c>bad-domain.com</c> in the blocklist matches
    /// <c>evil.bad-domain.com</c> too).
    /// </summary>
    public string TokenGrabberScannerBlocklist { get; set; } = "";

    public List<string> GetTokenGrabberScannerBlocklist() =>
        TokenGrabberScannerBlocklist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ─── Server protection: Honeypot trap channel ───────────────────
    /// <summary>
    /// Operating mode for the honeypot trap (feature #7). One of:
    ///   "Off"        — disabled. No rolling index, no trap, the warning
    ///                  embed is not posted.
    ///   "AlertOnly"  — default. On a hit, post to #alerts + write an audit
    ///                  row. The message is NOT deleted, the account is NOT
    ///                  banned, the counter does not move. Run here first to
    ///                  confirm a clean signal, then flip to Enforce.
    ///   "Enforce"    — ban the account, delete its honeypot message AND every
    ///                  message it posted server-wide in the last
    ///                  HoneypotPurgeWindowMinutes, bump the counter, and
    ///                  refresh the warning embed.
    /// </summary>
    public string HoneypotMode { get; set; } = "AlertOnly";

    /// <summary>
    /// The honeypot trap channel ID. Members must be able to see and send in
    /// this channel for the trap to function (an auto-posted, pinned warning
    /// embed deters humans). Manage Channel / Permissions / Webhooks / Create
    /// Invite should NOT be allowed to @everyone here.
    /// </summary>
    public ulong HoneypotChannelId { get; set; } = 1511103621507322036;

    /// <summary>
    /// DEPRECATED / NO LONGER CONSULTED. The honeypot now exempts only the
    /// server owner (who Discord will not let a bot ban); HQ and Administrators
    /// are intentionally NOT exempt, so a compromised officer account that posts
    /// in the trap is banned like any other. Retained only so existing
    /// appsettings.json files that still set this key continue to bind without
    /// error. Safe to remove from config.
    /// </summary>
    public string HoneypotExemptMinRank { get; set; } = "MAJ";

    /// <summary>
    /// How far back the cross-channel purge reaches on an Enforce hit, in
    /// minutes. Every message the banned account posted server-wide within the
    /// window is deleted from an in-memory rolling index. Clamped to a minimum
    /// of 1 internally.
    /// </summary>
    public int HoneypotPurgeWindowMinutes { get; set; } = 15;

    /// <summary>
    /// Whether the purge also clears the account's messages inside threads.
    /// Default true.
    /// </summary>
    public bool HoneypotIncludeThreadsInPurge { get; set; } = true;

    /// <summary>
    /// Optional banner image URL shown in the warning embed (host your own
    /// graphic in a Discord channel and paste its CDN URL here, the same way
    /// the PatrolWatch game thumbnails are hosted). Empty = no banner image.
    /// </summary>
    public string HoneypotImageUrl { get; set; } = "";

    /// <summary>
    /// Optional thumbnail URL for the warning embed. Empty = fall back to the
    /// guild icon (the 189th logo), so no setup is needed in the common case.
    /// </summary>
    public string HoneypotThumbnailUrl { get; set; } = "";

    // ─── Server protection: Cross-channel spam trap (behavioral) ─────
    /// <summary>
    /// Operating mode for the behavioral cross-channel spam trap (feature #8).
    /// Unlike the honeypot, this does NOT rely on a trap channel: it watches the
    /// same in-memory rolling message index the honeypot already maintains and
    /// fires when a single non-bot account posts in many DISTINCT channels
    /// within a few seconds — the signature of a compromised "self-bot" account
    /// blasting the server. This catches the spam pattern directly even when the
    /// account never happens to post in the honeypot channel. One of:
    ///   "Off"        — disabled. The detector does not run. (The rolling index
    ///                  still runs as long as the honeypot is enabled.)
    ///   "AlertOnly"  — default. On a trip, post to #alerts + write an audit
    ///                  row. No ban, no purge. Run here first to confirm the
    ///                  threshold produces no false positives, THEN flip to
    ///                  Enforce.
    ///   "Enforce"    — ban the account and purge every message it posted
    ///                  server-wide in the last HoneypotPurgeWindowMinutes
    ///                  (the purge window is shared with the honeypot), then
    ///                  write the audit row and alert.
    /// The server owner is never banned (Discord forbids a bot banning the
    /// owner); an owner that trips this trap is loudly alerted as a
    /// likely-compromised account instead — see the server-recovery runbook.
    /// The bot can still only ban members below it in the role hierarchy, same
    /// caveat as the honeypot.
    /// </summary>
    public string SpamTrapMode { get; set; } = "AlertOnly";

    /// <summary>
    /// Sliding window, in seconds, over which DISTINCT channels are counted for
    /// the cross-channel spam trap. Default 30. Clamped to a minimum of 1
    /// internally.
    /// </summary>
    public int SpamTrapWindowSeconds { get; set; } = 30;

    /// <summary>
    /// Number of DISTINCT channels a single account must post in within
    /// <see cref="SpamTrapWindowSeconds"/> to trip the spam trap. Default 5 —
    /// far above what a human can do by hand, so legitimate fast cross-posting
    /// (or an officer pasting into two or three channels) will not trip it.
    /// Clamped to a minimum of 2 internally.
    /// </summary>
    public int SpamTrapChannelThreshold { get; set; } = 5;

    // ─── Ban Hammer: "days since last ban" counter ───────────────────
    /// <summary>
    /// Master switch for the Ban Hammer feature. When false, the handler does
    /// nothing: no embed is posted, no ban events are counted, no refresh timer
    /// runs. Default true.
    /// </summary>
    public bool BanHammerEnabled { get; set; } = true;

    /// <summary>
    /// Channel the auto-updating "days since last ban" embed lives in. The bot
    /// posts the embed here once on first Ready and thereafter edits that same
    /// message in place (its ID is stored on
    /// <see cref="BotState.BanHammerMessageId"/>). 0 disables the feature.
    /// </summary>
    public ulong BanHammerChannelId { get; set; } = 1515136067898970152;

    /// <summary>
    /// Banner image/gif shown on the Ban Hammer embed. Placeholder by default
    /// until the server owner supplies the custom gif — set this to that URL
    /// (a Discord CDN attachment link works well, same as HoneypotImageUrl).
    /// Empty string hides the banner entirely.
    /// </summary>
    public string BanHammerImageUrl { get; set; } = "https://placehold.co/600x240/8B0000/FFFFFF/png?text=BAN+HAMMER";

    /// <summary>
    /// Thumbnail shown in the embed's corner. Empty (the default) falls back to
    /// the guild icon (the 189th logo), matching the honeypot embed.
    /// </summary>
    public string BanHammerThumbnailUrl { get; set; } = "";

    // ─── UFC / MMA Feed ──────────────────────────────────────────────
    /// <summary>
    /// Master switch for the UFC features. Controls the day-before
    /// <see cref="Services.UfcReminderService"/> reminder loop. The
    /// <c>/ufc-schedule</c> and <c>/ufc-results</c> commands work regardless of
    /// this flag; this gates only the automatic reminders. Default false so nothing
    /// posts until a channel is configured. UFC data comes from ESPN's keyless
    /// public MMA feed, so no API key is required.
    /// </summary>
    public bool UfcEnabled { get; set; } = false;

    /// <summary>
    /// Channel where day-before fight reminders are posted. Must be set (non-zero)
    /// and <see cref="UfcEnabled"/> true for reminders to fire.
    /// </summary>
    public ulong UfcChannelId { get; set; } = default;

    /// <summary>
    /// How many hours before an event the reminder fires. Default 24 (the day
    /// before). The reminder loop polls hourly, so the post lands within ~1h of
    /// the event entering this window.
    /// </summary>
    public int UfcReminderLeadHours { get; set; } = 24;

    /// <summary>
    /// When true (default), event embeds try to attach the official event poster
    /// resolved from Wikipedia. When false, only <see cref="UfcDefaultImageUrl"/>
    /// is used.
    /// </summary>
    public bool UfcUseWikipediaPoster { get; set; } = true;

    /// <summary>
    /// Fallback banner image used when no Wikipedia poster is found (or poster
    /// lookup is disabled). Empty means no image on the embed.
    /// </summary>
    public string UfcDefaultImageUrl { get; set; } = string.Empty;

    // ─── Palworld server ─────────────────────────────────────────────
    /// <summary>
    /// Master switch for the Palworld integration (presence feed, playtime
    /// tracking, /palworld-* commands, and the in-game event-reminder bridge).
    /// Default false — nothing runs until the server details are configured.
    /// Everything additionally requires PalworldBaseUrl + PalworldAdminPassword;
    /// see <see cref="Services.PalworldApiService.IsConfigured"/>.
    /// </summary>
    public bool PalworldEnabled { get; set; } = false;

    /// <summary>
    /// Base URL of the Palworld server's REST API, scheme and port included —
    /// e.g. "http://flywheel.dathost.net:29324". No trailing path: the client
    /// appends /v1/api itself.
    ///
    /// On DatHost the port is allocated automatically once the "Enable REST API"
    /// toggle is switched on in the server's Settings tab; hover the server IP in
    /// the panel and the REST API row shows the port (it sits just past the
    /// game/query/RCON ports). We use REST, not RCON: Pocketpair has deprecated
    /// RCON and it will stop working in a future update.
    /// </summary>
    public string PalworldBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The server's Admin Password — the REST API credential (HTTP Basic, with the
    /// literal username "admin").
    ///
    /// ⚠️ This is NOT the "Server Password" players type to join (that one sits
    /// directly above it in the DatHost Settings tab and is fine to share). This
    /// one grants kick/ban/save/shutdown over the whole server, and DatHost exposes
    /// the REST port to the open internet with no IP allowlisting, so this password
    /// is the ONLY thing protecting the server. It must be a long random string.
    ///
    /// ── How to set ──
    /// Never commit it. docker-compose maps:
    ///   BotConfig__PalworldAdminPassword=${PALWORLD_ADMIN_PASSWORD}
    /// Add PALWORLD_ADMIN_PASSWORD=... to the droplet's .env file.
    /// </summary>
    public string PalworldAdminPassword { get; set; } = string.Empty;

    /// <summary>
    /// How often PalworldPresenceService polls /players, in seconds. Default 60.
    /// Polling is not a design choice — the Palworld REST API has no webhooks, so
    /// joins/leaves can only be found by diffing snapshots. This also bounds the
    /// accuracy of recorded playtime. Clamped to 15–3600s.
    /// </summary>
    public int PalworldPollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether join/leave messages are posted to <see cref="PalworldFeedChannelId"/>.
    /// Turning this off still records sessions/playtime — it only silences the chat
    /// feed. Default true.
    /// </summary>
    public bool PalworldFeedEnabled { get; set; } = true;

    /// <summary>
    /// Channel for the Palworld join/leave feed (the #palworld channel). 0 disables
    /// the feed — sessions and playtime are still tracked either way, so turning it
    /// off only silences the chatter.
    /// </summary>
    public ulong PalworldFeedChannelId { get; set; } = 1526389577277771886;

    /// <summary>
    /// Whether the whole-server up/down notice is posted (as a green/red embed) to
    /// the feed channel when the server comes online or goes offline. Default true.
    /// Independent of the per-player join/leave feed — this can be silenced on its
    /// own. Still needs PalworldFeedEnabled + a channel (it shares the feed
    /// channel), and only fires after the same multi-poll offline delay the session
    /// logic uses, so a brief DatHost reboot won't trigger a down/up pair.
    /// SyncWithHandlers: PalworldPresenceService.PostServerStatusAsync.
    /// </summary>
    public bool PalworldServerStatusAnnounceEnabled { get; set; } = true;

    /// <summary>
    /// Role permitted to use /palworld-admin (announce, kick, ban, unban, save,
    /// restart). The dedicated "Palworld Mod" role: HQ plus the server's owner, who
    /// isn't in HQ but does own the box. Administrator always bypasses; 0 locks the
    /// command to Administrators only (fail closed, never fail open).
    ///
    /// Deliberately a ROLE ID rather than a rank floor: these commands can shut the
    /// game server down, so the people who can run them should be an explicit list,
    /// not "everyone at or above rank X" — a rank ladder would silently widen the
    /// blast radius every time someone gets promoted. And deliberately its own role
    /// rather than TicketHqRoleId, because the membership genuinely differs.
    /// SyncWithHandlers: PalworldCommandHandler.HasAdminRole.
    /// </summary>
    public ulong PalworldAdminRoleId { get; set; } = 1526650435396571266;

    /// <summary>
    /// When true, an event reminder also broadcasts in-game to whoever is on the
    /// Palworld server (via the REST /announce endpoint), so members deep in a Pal
    /// run don't miss an event just because they aren't looking at Discord. Default
    /// true; a no-op unless the Palworld feature is enabled and configured.
    /// SyncWithHandlers: EventReminderService.AnnounceInGameAsync.
    /// </summary>
    public bool PalworldEventAnnounceEnabled { get; set; } = true;

    // ─── SQLite Backup Settings ──────────────────────────────────────
    /// <summary>
    /// Master switch for SqliteBackupService. When false, the service exits
    /// immediately on startup — no snapshots, no Drive calls.
    /// </summary>
    public bool BackupEnabled { get; set; } = true;

    /// <summary>
    /// Hour of day (UTC, 0-23) for the nightly backup cycle. Default 5 — sits
    /// between AutoPromotionRunHourUtc (3) and RosterExportHourUtc (6) so the
    /// three scheduled jobs aren't competing for the same window. 1am ET.
    /// </summary>
    public int BackupHourUtc { get; set; } = 5;

    /// <summary>
    /// LEGACY — only used when BackupStorageProvider=GoogleDrive, and only
    /// works against a Workspace Shared Drive. Prefer R2 for new deploys.
    ///
    /// Google Drive folder ID for backup uploads. ... [keep existing comment]
    /// </summary>
    public string BackupDriveFolderId { get; set; } = string.Empty;

    /// <summary>
    /// Days of backup history to retain on Drive. Older files are deleted at
    /// the end of each successful cycle. Default 14 — enough buffer to notice
    /// a problem and roll back, not so much that storage grows without bound.
    /// Set to 0 to disable retention pruning entirely (files accumulate
    /// forever).
    /// </summary>
    public int BackupRetentionDays { get; set; } = 14;

    /// <summary>
    /// When true, the service runs the VACUUM INTO + gzip step locally but
    /// skips the Drive upload and retention prune. Useful for verifying the
    /// SQLite snapshot path works on a fresh deploy before pointing it at a
    /// real Drive folder. Recommended for first-run validation: set
    /// BackupDryRun=true, watch a cycle complete, check the logs for the
    /// snapshot size, flip to false.
    /// </summary>
    public bool BackupDryRun { get; set; } = false;

    /// <summary>
    /// Passphrase used to derive the AES-256-GCM key for backup encryption.
    /// When non-empty, every uploaded backup is encrypted with AES-256-GCM
    /// (PBKDF2-SHA256 key derivation, per-file random salt + nonce, format
    /// documented in <see cref="Services.SqliteBackupCrypto"/>) and the
    /// remote filename gets a ".enc" suffix.
    ///
    /// When empty (the default), backups upload unencrypted with a warning
    /// log line per cycle. Default-empty so first-time deployers of this
    /// change don't lose backups before they've set the passphrase.
    ///
    /// ── How to set ──
    /// In production this should flow from the host environment, not the
    /// committed appsettings.json. docker-compose maps:
    ///   BotConfig__BackupEncryptionPassphrase=${CLANGUARD_BACKUP_PASSPHRASE}
    /// Add CLANGUARD_BACKUP_PASSPHRASE=... to the droplet's .env file.
    ///
    /// ── Operational warning ──
    /// If you change or lose this passphrase you cannot decrypt prior
    /// backups. Store it in a password manager BEFORE setting it. Rotating
    /// to a new passphrase mid-stream means old backups stay readable with
    /// the old one and new backups with the new one — there is no
    /// re-encryption sweep, by design (re-encryption on rotation would
    /// require holding both passphrases at once and is rarely worth the
    /// complexity for a 14-day retention window).
    /// </summary>
    public string BackupEncryptionPassphrase { get; set; } = string.Empty;
    
    /// <summary>
    /// Which off-droplet storage backend SqliteBackupService uploads to.
    /// Default R2. Changing this requires a restart — read once at DI
    /// registration. Setting to GoogleDrive falls back to the legacy
    /// <see cref="Services.GoogleDriveBackupClient"/>, which only works
    /// against a Workspace Shared Drive.
    /// </summary>
    public BackupStorageProvider BackupStorageProvider { get; set; } = BackupStorageProvider.R2;

    /// <summary>
    /// Cloudflare R2 settings. Required when BackupStorageProvider=R2 and
    /// BackupDryRun=false. See <see cref="BackupR2Settings"/> for which
    /// fields go in appsettings.json vs. .env.
    /// </summary>
    public BackupR2Settings BackupR2 { get; set; } = new();

    // ─── Heartbeat Settings ──────────────────────────────────────────
    /// <summary>
    /// Master switch for HeartbeatService. When false, the service exits
    /// immediately on startup — no outbound pings.
    /// </summary>
    public bool HeartbeatEnabled { get; set; } = true;

    /// <summary>
    /// URL to ping on each heartbeat cycle. Typically a Healthchecks.io
    /// ping URL like https://hc-ping.com/{uuid}. When empty, the service
    /// logs a warning and exits — same fail-loud-not-silent pattern as
    /// SqliteBackupService when BackupDriveFolderId is unset.
    ///
    /// ── How to set ──
    /// The URL is effectively a secret (anyone with it can mark the check
    /// as healthy), so flow it through the host environment, not the
    /// committed appsettings.json. docker-compose maps:
    ///   BotConfig__HeartbeatPingUrl=${CLANGUARD_HEARTBEAT_URL}
    /// Add CLANGUARD_HEARTBEAT_URL=https://hc-ping.com/... to the droplet's .env file.
    /// </summary>
    public string HeartbeatPingUrl { get; set; } = string.Empty;

    /// <summary>
    /// Interval between heartbeat pings, in seconds. Default 60. Should be
    /// well under the Healthchecks.io check period so a single transient
    /// network blip between bot and HC.io doesn't trigger an alert — with
    /// 60s pings and a 1-minute period / 10-minute grace, the system
    /// tolerates ~10 missed pings before alerting. Floored to 10s in
    /// HeartbeatService to prevent accidental tight loops.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    // ─── Gateway watchdog ────────────────────────────────────────────

    /// <summary>
    /// Whether the gateway watchdog runs. Default true. The watchdog catches
    /// the "process alive but gateway wedged" failure mode that the heartbeat
    /// alone cannot — Discord.Net can report ConnectionState=Connected while
    /// the socket has actually stalled, so a state check isn't enough.
    /// </summary>
    public bool GatewayWatchdogEnabled { get; set; } = true;

    /// <summary>
    /// How long (seconds) the gateway may go with zero activity before the
    /// watchdog declares it wedged and exits the process for a Docker restart.
    /// Default 180. The truth signal is the gateway heartbeat ACK, surfaced as
    /// DiscordSocketClient.LatencyUpdated roughly every 41s; 180s is ~4 missed
    /// ACKs, comfortably past a single transient blip. Floored to 60s.
    /// </summary>
    public int GatewayWatchdogStallSeconds { get; set; } = 180;

    /// <summary>
    /// How often (seconds) the watchdog evaluates idle time. Default 30.
    /// Floored to 15s.
    /// </summary>
    public int GatewayWatchdogCheckSeconds { get; set; } = 30;

    // ─── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// True if invites created by this user ID should be excluded from
    /// the tracked-invite framework — neither surfaced in /invite list
    /// nor recorded in InviteJoins on attribution. Currently scoped to
    /// the Disboard bot, which creates short-lived invites as part of
    /// its /bump directory listing flow; those joins are operational
    /// noise rather than recruitment data and would otherwise pollute
    /// /invite stats and the briefing's recruitment-sources section.
    ///
    /// Hardcoded against DisboardBotId rather than a configurable list
    /// — if a future need arises to ignore another bot, this becomes a
    /// comma-separated config value at that point.
    /// </summary>
    public bool IsIgnoredInviter(ulong userId) => userId == DisboardBotId;

    /// <summary>
    /// Returns the exempt-roles list with ReserveRoleName appended (if set).
    /// AwolCheckService uses this so Reserve members are treated identically
    /// to Admin/Moderator/etc. — never assigned AWOL, never tracked. The
    /// kick command also re-checks Reserve explicitly as a belt-and-suspenders
    /// guard in case a Reserve role was added between AWOL assignment and kick.
    /// </summary>
    public List<string> GetExemptRolesList()
    {
        var list = ExemptRoles
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!string.IsNullOrWhiteSpace(ReserveRoleName)
            && !list.Contains(ReserveRoleName, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(ReserveRoleName);
        }

        return list;
    }

    public List<string> GetShortWindowRolesList() =>
        ShortWindowRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetRankRolesList() =>
        RankRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Parses TicketCategoriesCsv into a list of TicketCategoryDef. Each entry
    /// is "key|Label|emoji|routedRoleId|anonymousAllowed|defaultPriority";
    /// entries are separated by ';'. Malformed entries (wrong field count,
    /// bad role id) are skipped rather than throwing so one typo can't take
    /// the whole ticket panel down. RoutedRoleId 0 means "fall back to
    /// TicketHqRoleId" — resolved by the handler, not here.
    /// </summary>
    public List<TicketCategoryDef> GetTicketCategories()
    {
        var result = new List<TicketCategoryDef>();
        if (string.IsNullOrWhiteSpace(TicketCategoriesCsv)) return result;

        foreach (var raw in TicketCategoriesCsv.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = raw.Split('|');
            if (f.Length != 6) continue;

            var key   = f[0].Trim();
            var label = f[1].Trim();
            if (key.Length == 0 || label.Length == 0) continue;

            var emoji = f[2].Trim();
            _ = ulong.TryParse(f[3].Trim(), out var roleId);
            var anon = bool.TryParse(f[4].Trim(), out var a) && a;
            var priority = Enum.TryParse<SupportTicketPriority>(f[5].Trim(), true, out var p)
                ? p
                : SupportTicketPriority.Normal;

            result.Add(new TicketCategoryDef(key, label, emoji, roleId, anon, priority));
        }

        return result;
    }

    public List<string> GetPlatoonRolesList() =>
        PlatoonRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetAutoPromotionDryRunRanksList() =>
        AutoPromotionDryRunRanks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetAttendanceCountingSourcesList() =>
        AttendanceCountingSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Returns the appropriate window days for a guild member based on their roles.
    ///
    /// Logic: members holding a role in ShortWindowRoles (typically "Guest,RCT")
    /// get ShortWindowDays — UNLESS they also hold a higher-tier rank role
    /// (any RankRoles entry NOT in ShortWindowRoles, e.g. PVT and above), in
    /// which case the higher rank wins and they get the regular WindowDays.
    ///
    /// This handles Guest-promoted-to-PVT cases where the Guest role wasn't
    /// stripped on promotion: PVT takes precedence over Guest, so the user
    /// is correctly evaluated against the 28-day window rather than the
    /// 14-day Guest window. Members with no short-window role at all get
    /// WindowDays as before.
    /// </summary>
    public int GetWindowDaysForRoles(IEnumerable<string> memberRoleNames)
    {
        var roleList = memberRoleNames.ToList();
        var shortRoles = GetShortWindowRolesList();

        var hasShortWindowRole = roleList.Any(r =>
            shortRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        if (!hasShortWindowRole)
            return WindowDays;

        // Holds a short-window role. Check whether they also hold a higher-tier
        // rank role (any RankRoles entry NOT also in ShortWindowRoles). If so,
        // the higher rank takes precedence — Guest+PVT means PVT, not Guest.
        var rankRoles = GetRankRolesList();
        var hasHigherRank = roleList.Any(r =>
            rankRoles.Contains(r, StringComparer.OrdinalIgnoreCase)
            && !shortRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        return hasHigherRank ? WindowDays : ShortWindowDays;
    }
}