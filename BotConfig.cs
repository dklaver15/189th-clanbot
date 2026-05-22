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

    /// <summary>Name (or ID) of the HQ channel where AWOL notifications are posted.</summary>
    public string HqChannelName { get; set; } = "hq";

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
    /// The minimum rank required to use the /comp-event command.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string CompEventMinRank { get; set; } = "CPT";

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

    // ─── Server protection: Account-Age Gate ─────────────────────────
    /// <summary>
    /// Operating mode for the account-age gate on UserJoined. One of:
    ///   "Off"        — feature disabled.
    ///   "AlertOnly"  — post a security alert for any joiner whose account
    ///                  is younger than AccountAgeGateMinDays, but DO NOT
    ///                  kick. Default — non-destructive, observation-friendly.
    ///   "Kick"       — DM the joiner, kick them, post the alert with the
    ///                  kick outcome.
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