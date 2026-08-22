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
    /// ReserveRoleName is appended automatically by GetExemptRolesList(), so it
    /// does not need to be listed here.
    ///
    /// Kept deliberately short: this used to read "Admin,Moderator,Retired,Bot,
    /// Bot Whisperer", but RoleConfigValidator found on 2026-07-31 that four of
    /// those five roles do not exist in the guild. They matched nobody, so the
    /// list read as far more protective than it was. Only add a name here that
    /// is a real role, and check the startup report after changing it.
    ///
    /// Bots do not need an entry: the AWOL sweep skips member.IsBot before it
    /// ever looks at roles.
    /// </summary>
    public string ExemptRoles { get; set; } = "Moderator";

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
    public string RankRoles { get; set; } = "RCT,PVT,PFC,SPC,CPL,SGT,SSG,SFC,MSG,1SG,SGM,CSM,SMA,2ndLT,1stLT,CPT,MAJ,LTCOL,COL,BG,MG,LTG,GEN,GA";

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
    /// The role allowed to run /event view, the past-event RSVP lookup. Set to
    /// the HQ role.
    ///
    /// Deliberately a ROLE and not a rank threshold, for the same reason
    /// <see cref="XpAdjustRoleId"/> is. /event view is the one event command that
    /// reports on people rather than acting on an event: it lists who said they
    /// were coming and was then not credited for turning up. That is a smaller
    /// audience than the officers who can create events, and one whose membership
    /// should change by handing out a role rather than by a promotion.
    ///
    /// Its own key rather than a reuse of TicketHqRoleId, OfficerAppHqRoleId or
    /// XpAdjustRoleId: the ids happen to match today, and the concerns should be
    /// able to diverge without one silently changing the others.
    ///
    /// Fails CLOSED. If it names a role that does not exist in the guild, nobody
    /// but an Administrator can run the command and the handler logs why. Set to
    /// 0 to fall back to the ordinary <see cref="EventCommandMinRank"/> gate.
    /// </summary>
    public ulong EventViewRoleId { get; set; } = 1408089365816541264;

    // ── /reminder (scheduled announcements) ──

    /// <summary>
    /// Minimum rank that can schedule/manage reminders via /reminder. Default
    /// "2ndLT" — the same "any officer" floor as <see cref="EventCommandMinRank"/>.
    /// Administrator / Manage Roles always pass. Matched against a role name in
    /// RankRoles (case-insensitive).
    /// </summary>
    public string ReminderCommandMinRank { get; set; } = "2ndLT";

    /// <summary>
    /// Master switch for the reminder scheduler. When false,
    /// ReminderSchedulerService idles and no reminders post (rows are still
    /// created; they just don't fire until re-enabled).
    /// </summary>
    public bool RemindersEnabled { get; set; } = true;

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
    //  Post as the bot (/say)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Master switch for /say. When false the command still registers but
    /// refuses to open, which is the quickest way to shut it off without a
    /// redeploy of the command list.
    /// </summary>
    public bool SayEnabled { get; set; } = true;

    /// <summary>
    /// Minimum rank that can post a message as the bot via /say. Default "CPT",
    /// a step above the general officer floor: the post carries no author name,
    /// so it is deliberately a smaller circle than /reminder. Administrator /
    /// Manage Roles always pass. Must match a rank name in RankRoles
    /// (case-insensitive).
    /// </summary>
    public string SayCommandMinRank { get; set; } = "CPT";

    /// <summary>
    /// Channel that /say writes its audit entry to (who wrote it, where it
    /// went, and the full text). Defaults to the moderator notice log, the
    /// same channel the other operational alerts use -- HqChannelId points at
    /// #awol-list here, which is a review queue, not a place for bot audit
    /// trails. 0 falls back to <see cref="HqChannelId"/>.
    /// </summary>
    public ulong SayAuditChannelId { get; set; } = 1407959078105514046;

    // ──────────────────────────────────────────────────────────────────────
    //  Rank-loss notice
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, the bot posts a notice whenever a member who held a rank role
    /// ends up holding none. Nothing in ClanGuard takes a member's last rank
    /// role away, so this always reflects something done outside the bot: the
    /// rules-accept reaction role being un-toggled, a manual role edit, a role
    /// menu. Left unnoticed the member still looks ranked (the nickname prefix
    /// and any platoon role stay put) while counting as unranked for the AWOL
    /// window. Default true.
    /// </summary>
    public bool RankLossAlertEnabled { get; set; } = true;

    /// <summary>
    /// Channel the rank-loss notice posts to. Defaults to the moderator notice
    /// log, the same channel /say writes its audit entry to. Deliberately does
    /// NOT fall back to <see cref="HqChannelId"/>: that points at the AWOL
    /// review queue here, which is not a place for bot notices. 0 switches the
    /// notice off the same way RankLossAlertEnabled = false does.
    /// </summary>
    public ulong RankLossAlertChannelId { get; set; } = 1407959078105514046;

    /// <summary>
    /// Seconds to wait before posting the rank-loss notice, after which the
    /// member's roles are read again and the notice is dropped if a rank role
    /// came back. /promote and /demote strip the old rank role and then add the
    /// new one, so a normal promotion arrives as two gateway events and the
    /// member genuinely holds no rank role in between. Waiting and re-checking
    /// is what keeps every promotion from firing a false notice, so this must
    /// comfortably exceed the gap between those two REST calls.
    /// </summary>
    public int RankLossAlertGraceSeconds { get; set; } = 20;

    /// <summary>
    /// Minutes to suppress repeat rank-loss notices for the same member, so a
    /// role that flaps (someone clicking a reaction on and off) posts once
    /// rather than every time. 0 disables the suppression.
    /// </summary>
    public int RankLossAlertCooldownMinutes { get; set; } = 60;

    // ──────────────────────────────────────────────────────────────────────
    //  Rank reconcile
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When true, the bot repairs members who hold a rank role with no
    /// RankHistory row: on join, and on a periodic sweep. Rank tracking is
    /// otherwise driven entirely by live role-change events, and a role granted
    /// as part of the join (Discord's onboarding role picker does exactly this)
    /// produces no such event, so the member ends up ranked in Discord and
    /// unranked everywhere in the bot with nothing to fix it. Default true.
    /// </summary>
    public bool RankReconcileEnabled { get; set; } = true;

    /// <summary>
    /// When true the periodic sweep only reports what it would do. Default
    /// TRUE, on purpose. The join path always runs live and is safe: it repairs
    /// one member who just arrived. The sweep is the risky one, because on a
    /// server older than the bot it will find long-standing members whose rank
    /// predates rank tracking, and recording them all at once starts their
    /// promotion clocks together. Read the first run's log, then set this false
    /// if the list looks right.
    /// </summary>
    public bool RankReconcileSweepDryRun { get; set; } = true;

    /// <summary>Hours between reconcile sweeps. The sweep also runs at startup.</summary>
    public int RankReconcileSweepHours { get; set; } = 12;

    /// <summary>
    /// Seconds to wait after someone joins before checking whether their rank
    /// was recorded. Long enough that the live handler has had its chance and
    /// any onboarding roles have settled, so the reconcile only ever fires on a
    /// genuine miss rather than racing the normal path.
    /// </summary>
    public int RankReconcileJoinDelaySeconds { get; set; } = 45;

    /// <summary>
    /// How recently a member must have joined for a repair to also apply the
    /// RCT nickname prefix and write the recruit-log row. Outside this window
    /// the repair records the rank and nothing else: a sweep over an established
    /// server would otherwise rename members who have gone years without a
    /// prefix and file each of them as a new recruit.
    /// </summary>
    public int RankReconcileSideEffectWindowHours { get; set; } = 48;

    /// <summary>
    /// Furthest back a repaired RankHistory row may be dated. AssignedAt drives
    /// every promotion calculation, so a row dated years ago would hand that
    /// member every event since as credit toward their next promotion. Anything
    /// resolving older than this is dated to the reconcile instead. Discord's
    /// audit log only retains 45 days anyway, which is where the default comes
    /// from.
    /// </summary>
    public int RankReconcileMaxBackdateDays { get; set; } = 45;

    /// <summary>
    /// Channel the "rank recorded after the fact" notice posts to. Defaults to
    /// the moderator notice log, alongside the rank-loss notice. 0 records the
    /// repair in the bot log only.
    /// </summary>
    public ulong RankReconcileNoticeChannelId { get; set; } = 1407959078105514046;

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

    // ──────────────────────────────────────────────────────────────────────
    //  Joke of the Day (/jotd)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum rank required to post a Joke of the Day via /jotd. Must match a
    /// rank name in RankRoles (case-insensitive). Default "SGT". Admins / users
    /// with Manage Roles always pass regardless of rank.
    /// </summary>
    public string JotdMinRank { get; set; } = "SGT";

    /// <summary>
    /// Channel the /jotd embed is posted to. Defaults to the same channel as
    /// /qotd (the clan's general channel).
    /// </summary>
    public ulong JotdChannelId { get; set; } = 1421928902963494922;

    // ──────────────────────────────────────────────────────────────────────
    //  Polls (/poll)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Channel that gets the "new poll" announcement embed and the midpoint
    /// "still open" reminder for every poll, wherever the poll itself was posted.
    /// Polls usually live in a low-traffic #polls channel, so the clan only finds
    /// out about them if something surfaces them in the channel people actually
    /// read — this is that channel (defaults to the clan's general channel).
    ///
    /// Set to 0 to disable cross-posting entirely: the announcement is skipped and
    /// the reminder falls back to the poll's own channel (the pre-cross-post
    /// behaviour). Cross-posting is also skipped automatically when a poll is
    /// posted directly into this channel, so it never announces itself.
    /// </summary>
    public ulong PollAnnounceChannelId { get; set; } = 1421928902963494922;

    /// <summary>
    /// Master switch for poll cross-posting. When false, nothing is posted to
    /// <see cref="PollAnnounceChannelId"/> and the midpoint reminder stays in the
    /// poll's own channel — equivalent to setting the channel id to 0, but kept
    /// separate so the channel can be configured and toggled independently.
    /// </summary>
    public bool PollAnnounceEnabled { get; set; } = true;

    /// <summary>
    /// Whether the cross-posted announcement lists the poll's options. Off keeps
    /// the announcement a one-line nudge (question + jump link); on lets members
    /// see what they'd be choosing between before clicking through.
    /// </summary>
    public bool PollAnnounceShowOptions { get; set; } = true;

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

    /// <summary>
    /// How many days a FAILED recording keeps its captured audio before the
    /// retention sweep reclaims it.
    ///
    /// This used to be zero: a failed row's audio was deleted on the very next
    /// sweep. On 2026-08-09 the transcriber sidecar was being OOM-killed mid-run,
    /// so two meetings sat in Transcribing until the 6h cap failed them out, and
    /// minutes later their audio was gone. Both recordings were unrecoverable and
    /// there was nothing left to diagnose from. A failure is nearly always an
    /// infrastructure problem worth retrying, so the audio now outlives the row's
    /// failure by default. Set 0 to restore the old delete-immediately behaviour.
    /// </summary>
    public int MeetingFailedAudioRetentionDays { get; set; } = 7;

    /// <summary>
    /// Channel ID for meeting-pipeline failure notices. 0 falls back to
    /// <see cref="HqChannelId"/>; if that is also unset nothing is posted and the
    /// failure stays in the log, which is exactly the blind spot that let the
    /// pipeline fail silently from 2026-06-28 to 2026-08-10.
    /// </summary>
    public ulong MeetingFailureAlertChannelId { get; set; } = default;

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

    /// <summary>
    /// How many consecutive failed polls before DiscordStatusMonitorService
    /// escalates to the alert channel. Failed polls retry every minute, so this
    /// is roughly "minutes broken before anyone is told". Default 10.
    ///
    /// Set to 0 to disable escalation entirely; the consecutive-failure counter
    /// on /health keeps working either way.
    /// </summary>
    public int DiscordStatusFailureAlertThreshold { get; set; } = 10;

    /// <summary>
    /// Where DiscordStatusMonitorService posts its "polling has been failing"
    /// escalation and the matching recovery notice. Defaults to 0, which falls
    /// back to <see cref="HqChannelId"/>. Kept separate so this one alert can be
    /// routed elsewhere without moving everything else out of HQ.
    ///
    /// Deliberately NOT DiscordStatusChannelId: that channel is for incident
    /// announcements the whole server reads, and "the bot's monitor is broken"
    /// is an officer problem, not a member-facing one.
    /// </summary>
    public ulong DiscordStatusAlertChannelId { get; set; } = default;

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

    // ─── Sleeper fantasy football ────────────────────────────────────
    /// <summary>
    /// Master switch for the fantasy football feature: the /sleeper-standings,
    /// /sleeper-matchups and /sleeper-link commands, and the weekly matchup posts.
    ///
    /// Sleeper's read API takes no key, no token and no OAuth, so this switch plus
    /// <see cref="SleeperLeagueId"/> is the entire setup. Nothing here can write to
    /// Sleeper: no roster moves, no lineup changes, no trades. Default false, opt-in.
    /// </summary>
    public bool SleeperEnabled { get; set; } = false;

    /// <summary>
    /// The Sleeper league id, an 18-digit number. Found at the end of the league's
    /// URL on sleeper.com, or in the mobile app under league settings.
    ///
    /// This changes every season: Sleeper creates a NEW league id when a league is
    /// rolled over to the next year, and the old id keeps serving the old season's
    /// data forever rather than erroring. A stale value here therefore shows last
    /// season's standings quite happily, so it has to be updated each year.
    /// </summary>
    public string SleeperLeagueId { get; set; } = string.Empty;

    /// <summary>
    /// Channel the weekly matchup preview, live scoreboard and recap are posted to.
    /// 0 disables all three, leaving the slash commands working anywhere.
    /// </summary>
    public ulong SleeperChannelId { get; set; } = default;

    /// <summary>
    /// Role allowed to link a Sleeper account on someone else's behalf. 0 means
    /// only server administrators can. Anyone can always link themselves.
    /// </summary>
    public ulong SleeperAdminRoleId { get; set; } = default;

    /// <summary>
    /// Post the week's matchups once, when a new NFL week opens. Skipped for a week
    /// that already has scores when the bot first sees it, since a preview after
    /// kickoff is just a worse scoreboard. Default true.
    /// </summary>
    public bool SleeperMatchupPreviewEnabled { get; set; } = true;

    /// <summary>
    /// Maintain a single live scoreboard message, edited in place while games are
    /// on. The edit is skipped when no score moved since the previous cycle.
    /// Default true.
    /// </summary>
    public bool SleeperLiveScoresEnabled { get; set; } = true;

    /// <summary>
    /// Post a results recap once the NFL clock moves past a week the bot covered:
    /// final scores plus the week's high score, closest game and biggest blowout.
    /// Default true.
    /// </summary>
    public bool SleeperRecapEnabled { get; set; } = true;

    /// <summary>
    /// How often (minutes) the background service checks for score changes. Floored
    /// at 5 in code, which keeps the bot far inside Sleeper's stated limit of 1000
    /// requests per minute. Default 10.
    /// </summary>
    public int SleeperRefreshIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Render a linked member's Discord mention next to their team name on fantasy
    /// surfaces. Turn off to show Sleeper display names only. Default true.
    /// </summary>
    public bool SleeperMentionLinkedMembers { get; set; } = true;

    /// <summary>
    /// Whether the weekly posts actually NOTIFY the people they mention.
    ///
    /// Default false: mentions still render as clickable names, they just do not
    /// fire a notification. Sixteen pings a week from a side activity is how a
    /// channel gets muted. Set true if the league wants the nudge.
    /// </summary>
    public bool SleeperPingOnPost { get; set; } = false;

    /// <summary>
    /// Keep the NFL player directory in memory so scores can name who put them up
    /// (the recap's "top performer" line). Sleeper's directory is a single ~5 MB
    /// document, refreshed at most daily as Sleeper asks; the trimmed copy the bot
    /// retains is a few MB. Turn off to save that RAM and lose only that one line.
    /// Default true.
    /// </summary>
    public bool SleeperPlayerCacheEnabled { get; set; } = true;

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

    /// <summary>
    /// Channel ID where RoleConfigValidator posts its startup report when the
    /// role configuration does not match the guild's actual roles (a rank role
    /// missing from RankRoles, a *MinRank that is not in RankRoles, and so on).
    /// Defaults to the moderator notice log, the same channel the other
    /// operational alerts use. When set to 0 the bot falls back to
    /// SecurityAlertsChannelId; if neither resolves the problems are still in
    /// the startup log and on /health, the channel post is just skipped.
    /// </summary>
    public ulong RoleConfigAlertChannelId { get; set; } = 1407959078105514046;

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
    /// Whether the per-player join/leave messages are posted to
    /// <see cref="PalworldFeedChannelId"/>. Scoped to ONLY that chatter: turning it
    /// off still records sessions/playtime, and does NOT affect the server up/down
    /// notice (<see cref="PalworldServerStatusAnnounceEnabled"/>) or the lag alerts
    /// (<see cref="PalworldLagAlertEnabled"/>) — those share the channel but have
    /// their own switches. Default false: the join/leave stream tended to clutter
    /// the channel, so it's opt-in.
    /// </summary>
    public bool PalworldFeedEnabled { get; set; } = false;

    /// <summary>
    /// Channel for the Palworld join/leave feed (the #palworld channel). 0 disables
    /// the feed — sessions and playtime are still tracked either way, so turning it
    /// off only silences the chatter.
    /// </summary>
    public ulong PalworldFeedChannelId { get; set; } = 1526389577277771886;

    /// <summary>
    /// Whether each poll also records a <see cref="PalworldMetricSample"/> (server
    /// FPS, player count, uptime). Default true. This is what powers
    /// /palworld-performance and the lag alerts; with it off, both go blind.
    /// </summary>
    public bool PalworldMetricsSamplingEnabled { get; set; } = true;

    /// <summary>
    /// Days of health samples to keep. Default 30 (~43k rows at a 60s cadence —
    /// negligible for SQLite). Older rows are pruned periodically. 0 disables
    /// pruning entirely (rows accumulate forever).
    /// </summary>
    public int PalworldMetricsRetentionDays { get; set; } = 30;

    /// <summary>
    /// Whether a lag alert is posted to the feed channel when server FPS stays
    /// below <see cref="PalworldLagAlertFpsThreshold"/>. Default true.
    /// </summary>
    public bool PalworldLagAlertEnabled { get; set; } = true;

    /// <summary>
    /// Server FPS at or below which the server is considered to be struggling.
    /// Default 30 — half of the ~60 a healthy Palworld server holds, and low enough
    /// that players will already be feeling it.
    /// </summary>
    public int PalworldLagAlertFpsThreshold { get; set; } = 30;

    /// <summary>
    /// Consecutive low-FPS samples required before alerting. Default 3, i.e. ~3
    /// minutes at the default poll interval. A single bad tick is meaningless —
    /// a world save or a raid spawn can dip FPS for one sample — so the alert is
    /// deliberately about SUSTAINED degradation.
    /// </summary>
    public int PalworldLagAlertConsecutiveSamples { get; set; } = 3;

    /// <summary>
    /// Minimum minutes between lag alerts, so a server that spends an evening
    /// hovering at the threshold posts once rather than continuously. Default 60.
    /// The recovery notice is not rate-limited (it can only follow an alert).
    /// </summary>
    public int PalworldLagAlertCooldownMinutes { get; set; } = 60;

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
    /// SyncWithHandlers: EventReminderService.AnnouncePalworldAsync.
    /// </summary>
    public bool PalworldEventAnnounceEnabled { get; set; } = true;

    // ─── Satisfactory server ─────────────────────────────────────────

    /// <summary>
    /// The Palworld announce behaviour, for Satisfactory: an event reminder also
    /// goes into the in-game chat via FRM's sendChatMessage, as an A.D.A.
    /// message so it reads as part of the game rather than as another chat line.
    ///
    /// Default true, but a no-op unless Satisfactory and FRM are both enabled
    /// AND <see cref="FrmAuthToken"/> is set — FRM rejects writes without it,
    /// even though every read works fine. That case logs a warning once, because
    /// it is otherwise indistinguishable from the feature not existing.
    /// SyncWithHandlers: EventReminderService.AnnounceSatisfactoryAsync.
    /// </summary>
    public bool SatisfactoryEventAnnounceEnabled { get; set; } = true;
    /// <summary>
    /// Master switch for the Satisfactory integration (status command, presence
    /// feed, /satisfactory-* commands). Default false — nothing runs until the
    /// server details are configured. Everything additionally requires
    /// SatisfactoryBaseUrl plus a credential (an admin password OR a pre-generated
    /// API token); see <see cref="Services.SatisfactoryApiService.IsConfigured"/>.
    /// </summary>
    public bool SatisfactoryEnabled { get; set; } = false;

    /// <summary>
    /// Base URL of the Satisfactory Dedicated Server's HTTPS API, scheme + host +
    /// port, no trailing path — e.g. "https://1.2.3.4:7777". The client appends
    /// "/api/v1" itself.
    ///
    /// ── Where the port comes from ──
    /// Unlike Palworld, Satisfactory's API is NOT on a separate allocated port: it
    /// rides the SAME port players type into the game to connect (the game port,
    /// 7777 by default). So the IP:port your members enter in-game IS this URL —
    /// just prefix "https://". The scheme MUST be https: the API is always TLS, even
    /// when the server uses a self-signed certificate (which the client accepts; see
    /// <see cref="Services.SatisfactoryApiService"/>).
    /// </summary>
    public string SatisfactoryBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The server's Admin Password. The bot exchanges it for a short-lived Bearer
    /// token via the API's PasswordLogin function (privilege level Administrator),
    /// caches that token, and silently re-logs in when it expires. Leave blank if you
    /// instead set <see cref="SatisfactoryApiToken"/>.
    ///
    /// ⚠️ This grants save/shutdown/run-command over the whole server. Never commit
    /// it. docker-compose maps:
    ///   BotConfig__SatisfactoryAdminPassword=${SATISFACTORY_ADMIN_PASSWORD}
    /// Add SATISFACTORY_ADMIN_PASSWORD=... to the droplet's .env file.
    /// </summary>
    public string SatisfactoryAdminPassword { get; set; } = string.Empty;

    /// <summary>
    /// Optional pre-generated Application API token, an alternative to
    /// <see cref="SatisfactoryAdminPassword"/>. Generated once on the server console
    /// with <c>server.GenerateAPIToken</c>; unlike a password login it never expires
    /// and needs no re-authentication. When set, it takes precedence over the admin
    /// password. Same secret-handling rules: never commit it, inject from the
    /// environment. Leave blank to use the admin password instead.
    /// </summary>
    public string SatisfactoryApiToken { get; set; } = string.Empty;

    /// <summary>
    /// How often SatisfactoryPresenceService polls the server state, in seconds.
    /// Default 60. Like Palworld, the API has no webhooks, so up/down and player-count
    /// changes can only be found by polling. Clamped to 15–3600s.
    /// </summary>
    public int SatisfactoryPollIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Whether the presence feed (server up/down + player-count changes) is posted to
    /// <see cref="SatisfactoryFeedChannelId"/>. Default true. Note the Satisfactory
    /// API exposes only a player COUNT, not names, so this feed reports "N players
    /// online", never per-player join/leave lines like the Palworld feed does.
    /// </summary>
    public bool SatisfactoryFeedEnabled { get; set; } = true;

    /// <summary>
    /// Channel for the Satisfactory presence feed. 0 disables the feed. Set this to
    /// the #satisfactory channel id.
    /// </summary>
    public ulong SatisfactoryFeedChannelId { get; set; } = 0;

    /// <summary>
    /// Whether the whole-server up/down notice (green/red embed) is posted to the
    /// feed channel. Default true. Shares the feed channel and only fires after the
    /// same multi-poll offline delay the presence logic uses, so a brief host reboot
    /// won't trigger a down/up pair.
    /// SyncWithHandlers: SatisfactoryPresenceService.PostServerStatusAsync.
    /// </summary>
    public bool SatisfactoryServerStatusAnnounceEnabled { get; set; } = true;

    /// <summary>
    /// The "Satisfactory Mod" role. Administrator always bypasses; 0 fails CLOSED
    /// (Administrators only), never open.
    ///
    /// <para>Now gates only /satisfactory-link on someone else's behalf. It used to
    /// gate /satisfactory-admin save|restart|command, which were removed 2026-07-28
    /// in favour of the host's own Discord bot. Kept because the role still exists and
    /// the link case still needs it.</para>
    /// SyncWithHandlers: SatisfactoryCommandHandler.HasAdminRole.
    /// </summary>
    public ulong SatisfactoryAdminRoleId { get; set; } = 0;

    /// <summary>
    /// Display name of the game host's control panel, used in the "I can't reach the
    /// server" messages so members are pointed somewhere that can actually help.
    ///
    /// <para>Why this exists: the game's HTTPS API lives INSIDE the Satisfactory
    /// process, so the bot can only ever talk to a server that is already running. A
    /// crashed or hung one can't be reached, let alone restarted. That needs the host
    /// panel, and nothing in the bot can reach one: broccoli confirmed 2026-07-28 that
    /// they have no customer API yet. Rather than hardcode a host name that goes stale
    /// the next time the clan moves, the messages read it from config.</para>
    ///
    /// Empty falls back to the neutral phrase "the host control panel".
    /// SyncWithHandlers: SatisfactoryCommandHandler.PanelHint.
    /// </summary>
    public string SatisfactoryHostPanelName { get; set; } = string.Empty;

    /// <summary>
    /// Link to the host control panel's dashboard, rendered as a markdown link on
    /// <see cref="SatisfactoryHostPanelName"/>. Empty = name only, no link.
    /// SyncWithHandlers: SatisfactoryCommandHandler.PanelHint.
    /// </summary>
    public string SatisfactoryHostPanelUrl { get; set; } = string.Empty;

    /// <summary>
    /// Slash command exposed by the HOST's own Discord bot that restarts the server
    /// from their side, e.g. <c>/restart</c> from the Indifferent Broccoli Server
    /// Manager Bot. Named in the offline hint so a member who can't reach the game
    /// server has a fix they can run without leaving Discord.
    ///
    /// <para>broccoli confirmed 2026-07-28 that they have no customer-facing API
    /// "yet" and pointed at that bot instead, so this is the only panel-side restart
    /// available to us. Set it only once the host's bot is actually in the guild:
    /// empty means the hint just points at the dashboard, which is the honest answer
    /// when there's no bot to run the command on.</para>
    ///
    /// SyncWithHandlers: SatisfactoryCommandHandler.OfflineHint.
    /// </summary>
    public string SatisfactoryHostBotCommand { get; set; } = string.Empty;

    // ─── Ficsit Remote Monitoring (Satisfactory mod) ─────────────────
    /// <summary>
    /// Master switch for the FRM integration. FRM is a server-side Satisfactory
    /// mod whose HTTP API exposes what the game's own API cannot — above all
    /// PLAYER NAMES, plus production, power and mod-list data.
    ///
    /// Additive on top of <see cref="SatisfactoryEnabled"/>: with this off, the
    /// Satisfactory feature behaves exactly as before (player counts only).
    /// </summary>
    public bool FrmEnabled { get; set; } = false;

    /// <summary>
    /// Base URL of FRM's own web server — <c>http://host:port</c>, no trailing
    /// slash, plain HTTP (it does not do TLS).
    ///
    /// <para>This is NOT the game port. FRM listens on its own TCP port, set by
    /// <c>FicsitRemoteMonitoring.Server.uWS.Port</c> in the server's
    /// GameUserSettings.ini. It defaults to 8080, which shared hosts rarely
    /// allow — the clan's host had to publish port 27052 to the container before
    /// the mod could bind at all. If reads start failing, check the server log
    /// for "Attempting to listen on port N" followed by "Bind failed".</para>
    /// </summary>
    public string FrmBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// FRM's API token, for the five write endpoints (sendChatMessage,
    /// createPing, setEnabled, setSwitches, setModSetting). Reads need no auth.
    ///
    /// Auto-generated by the mod at load and written to
    /// <c>FicsitRemoteMonitoring.Server.uWS.AuthenticationToken</c> in the
    /// server's GameUserSettings.ini; blanking that entry makes it regenerate.
    ///
    /// ⚠️ Secret — never commit. Anyone holding it can switch machines and power
    /// off, since FRM's web server is unauthenticated for reads and plain HTTP.
    /// docker-compose maps:
    ///   BotConfig__FrmAuthToken=${FRM_AUTH_TOKEN}
    /// </summary>
    public string FrmAuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Master switch for the factory ALERT feed (tripped fuses, batteries running
    /// down). Posts to <see cref="SatisfactoryFeedChannelId"/>. Requires FRM.
    /// </summary>
    public bool SatisfactoryAlertsEnabled { get; set; } = true;

    /// <summary>
    /// How often to poll getPower for alerts, in seconds. Clamped 30–3600.
    /// Default 120: fast enough that a blown fuse is noticed within a couple of
    /// minutes, slow enough not to hammer a game server.
    /// </summary>
    public int SatisfactoryAlertIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Minimum gap between repeat alerts for the SAME circuit, in minutes.
    /// Stops a flapping fuse spamming the channel. Recovery messages ignore this
    /// — "it's fixed" should never be suppressed.
    /// </summary>
    public int SatisfactoryAlertCooldownMinutes { get; set; } = 30;

    /// <summary>
    /// Battery charge (%) at or below which a circuit is reported as running
    /// down. Recovery is announced once it climbs 10 points clear of this, so a
    /// bank hovering at the threshold doesn't alternate alert/recovery forever.
    /// Only applies to circuits that actually have batteries.
    /// </summary>
    public double SatisfactoryBatteryAlertPercent { get; set; } = 20;

    /// <summary>
    /// Master switch for the daily factory digest. Off by default — it's a
    /// recurring public post, so it should be switched on deliberately once the
    /// channel is chosen.
    /// </summary>
    public bool SatisfactoryDigestEnabled { get; set; } = false;

    /// <summary>
    /// Hour (0–23) at which the daily digest posts, in
    /// <see cref="SatisfactoryDigestTimeZone"/>. Fires once per LOCAL day.
    ///
    /// <para>Replaces the old SatisfactoryDigestHourUtc. A fixed UTC hour drifts
    /// an hour at every DST changeover, so a "9am" digest was only ever right
    /// for half the year.</para>
    ///
    /// <para>The digest still posts if the bot misses the exact hour — it will
    /// go out any time within three hours of the slot, so a redeploy at 8:58am
    /// doesn't cost the day's report.</para>
    /// </summary>
    public int SatisfactoryDigestHour { get; set; } = 9;

    /// <summary>
    /// IANA timezone for everything Satisfactory: the hour the digest fires,
    /// the day boundaries its "yesterday" window and the playtime chart use, and
    /// the labels on chart time axes.
    ///
    /// <para><b>Deliberately one setting, not several.</b> Splitting "when
    /// things happen" from "how times are displayed" reads tidier but lets them
    /// drift — and a chart whose day boundaries disagree with the report printed
    /// beside it is worse than either being wrong on its own.</para>
    ///
    /// <para>An unrecognised id falls back to UTC with a warning rather than
    /// throwing — a config typo shouldn't take down the power alerts that share
    /// the service.</para>
    /// </summary>
    public string SatisfactoryDigestTimeZone { get; set; } = "America/New_York";

    /// <summary>
    /// Channel for the daily digest. 0 = use
    /// <see cref="SatisfactoryFeedChannelId"/>. A report and a join/leave feed
    /// often want different homes, hence the separate key.
    /// </summary>
    public ulong SatisfactoryDigestChannelId { get; set; } = 0;

    /// <summary>
    /// Master switch for the milestone / M.A.M. research feed. Announces things
    /// the clan completes, once each, the first time they're seen done.
    ///
    /// The first run on a save records everything already complete WITHOUT
    /// announcing, so switching this on won't dump the back catalogue into a
    /// channel.
    /// </summary>
    public bool SatisfactoryUnlockFeedEnabled { get; set; } = false;

    /// <summary>
    /// Channel for milestone/research announcements. 0 = use
    /// <see cref="SatisfactoryFeedChannelId"/>.
    /// </summary>
    public ulong SatisfactoryUnlockChannelId { get; set; } = 0;

    /// <summary>
    /// Seconds between M.A.M. research polls. Clamped 60–3600, default 300.
    /// getResearchTrees is ~72 KB on the clan's server — cheap enough for a
    /// five-minute cadence, though it does run on the game thread.
    /// </summary>
    public int SatisfactoryResearchPollSeconds { get; set; } = 300;

    /// <summary>
    /// Seconds between milestone (schematic) polls. Clamped 300–21600, default
    /// 1800.
    ///
    /// Deliberately much slower than the research poll: getSchematics is
    /// <b>1.1 MB</b> across 575 entries on the clan's server because every
    /// schematic embeds its full recipe list, and FRM serves it from the GAME
    /// THREAD. A milestone announced half an hour late is still good news; a
    /// stuttering server is not.
    /// </summary>
    public int SatisfactoryMilestonePollSeconds { get; set; } = 1800;

    /// <summary>
    /// Whether to record a power sample on each alert poll, for the charts.
    ///
    /// On by default because it's genuinely close to free: the alert loop
    /// already calls getPower, so this adds a row insert and NOT a request to
    /// the game server. Switch it off to stop the table growing at all.
    /// </summary>
    public bool SatisfactoryMetricsEnabled { get; set; } = true;

    /// <summary>
    /// How many days of power samples to keep. Clamped 1–365, default 30.
    /// At the default 120s alert cadence that's ~720 rows/day, so 30 days is
    /// ~21,600 rows.
    /// </summary>
    public int SatisfactoryMetricsRetentionDays { get; set; } = 30;

    // ─── Satisfactory: rail network ──────────────────────────────────
    // All of this rides FRM's rail endpoints. Everything here is idle when
    // FrmEnabled is false, regardless of these values.
    // SyncWithHandlers: SatisfactoryTrainWatch, SatisfactoryRailMapRenderer,
    // SatisfactoryCommandHandler (/satisfactory-trains).

    /// <summary>
    /// Whether to announce derailed and stopped trains in the feed channel.
    ///
    /// <para>Rides <see cref="SatisfactoryAlertsEnabled"/>'s poll, so switching
    /// the power alerts off switches these off too. This key only controls
    /// whether the rail half of that tick says anything.</para>
    /// </summary>
    public bool SatisfactoryTrainAlertsEnabled { get; set; } = true;

    /// <summary>
    /// How long a train with a route has to sit still before it counts as
    /// stopped. Clamped 2–240, default 10.
    ///
    /// <para>The floor is 2 rather than 0 because a train loading at a platform
    /// is legitimately stationary, and a window shorter than a normal dwell
    /// would announce every delivery. Ten minutes is far past any dwell and
    /// still well inside "somebody should look at this".</para>
    /// </summary>
    public int SatisfactoryTrainStuckMinutes { get; set; } = 10;

    /// <summary>
    /// Minutes between station reads. Clamped 1–240, default 10.
    ///
    /// <para>Deliberately slower than the train poll. getTrains is one small
    /// object per train; getTrainStation carries a full inventory per platform
    /// and grows with the network. Buffer levels also move on the timescale of
    /// a train round trip, so reading them every two minutes would cost more
    /// and say the same thing.</para>
    /// </summary>
    public int SatisfactoryTrainStationPollMinutes { get; set; } = 10;

    /// <summary>
    /// Fill percentage at or below which a LOADING platform counts as starved.
    /// Default 5.
    /// </summary>
    public double SatisfactoryStationStarvedPercent { get; set; } = 5;

    /// <summary>
    /// Fill percentage at or above which a platform counts as backed up.
    /// Default 95.
    /// </summary>
    public double SatisfactoryStationBackedUpPercent { get; set; } = 95;

    /// <summary>
    /// How long the rail map holds its track geometry. Clamped 1–1440,
    /// default 60.
    ///
    /// <para>getTrainRails is the heaviest endpoint this bot touches (one object
    /// per rail segment, each with a spline point list) and track only changes
    /// when somebody builds. Trains and stations are always read fresh, so a
    /// long cache costs an out-of-date piece of NEW track and nothing else.</para>
    /// </summary>
    public int SatisfactoryRailMapCacheMinutes { get; set; } = 60;

    // ─── Satisfactory: factory map ───────────────────────────────────────
    // SyncWithHandlers: SatisfactoryFactoryMapRenderer, FrmApiService,
    // SatisfactoryCommandHandler (/satisfactory-map).

    /// <summary>
    /// Ceiling on a single heavy FRM response, in megabytes. Clamped 1–256,
    /// default 16.
    ///
    /// <para>getFactory, getBelts and getResourceNode grow with the save,
    /// without bound, and this bot runs on a 1 vCPU / 2 GB droplet where RAM is
    /// the binding constraint. Peak usage is roughly twice this while a response
    /// is being read, so 16 MB is about 32 MB transient, which the box absorbs.
    /// Refusing to draw a map is a much better failure than the process dying
    /// and taking the AWOL sweep and the event scheduler with it.</para>
    ///
    /// <para>Raise it if the factory outgrows the limit and the droplet has
    /// headroom. The log names this key when it bites.</para>
    /// </summary>
    public int FrmMaxResponseMegabytes { get; set; } = 16;

    /// <summary>
    /// How long the factory map holds its geometry. Clamped 1–1440, default 15.
    ///
    /// <para>Shorter than the rail cache because machines change state (idle,
    /// paused, unconfigured) far more often than track gets built, and state is
    /// most of what the map is for. Belts, pipes and resource nodes are cached
    /// separately and for longer inside the renderer, since those really are
    /// geometry.</para>
    /// </summary>
    public int SatisfactoryFactoryMapCacheMinutes { get; set; } = 15;

    /// <summary>
    /// Default radius in metres for <c>/satisfactory-map focus:</c>. Clamped
    /// 10–5000, default 250.
    ///
    /// <para>250 m is about 30 foundations across, which frames one production
    /// block. The whole point of focus mode is that a machine is 8 m and a
    /// fitted view of a kilometre-scale base renders it smaller than a pixel.</para>
    /// </summary>
    public int SatisfactoryMapFocusRadiusMetres { get; set; } = 250;

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

    // ─── XP ladder / seasonal leaderboard ────────────────────────────
    //
    // A recognition-and-engagement layer, NOT a promotion mechanism. The clan
    // reviewed both options (see XP_Promotion_Handout.pdf) and chose Proposal B:
    // XP drives levels, a seasonal leaderboard and bragging rights, while
    // promotions stay with event-attendance credit and officer judgement. No XP
    // value here is read by PromotionService or AutoPromotionService.
    //
    // ── How the economy is balanced ──
    // Events dominate by design. At the defaults an active member (2 ops/week,
    // a meeting a fortnight, ~4h of voice, 5 messages/day) earns ~1,455 XP/week,
    // of which ~84% comes from ops. Someone who never attends anything but maxes
    // out chat AND idles in voice every single day tops out around 380 XP/week —
    // so grinding chat can never overtake simply showing up. Retune with that
    // ratio in mind: if the chat + voice ceilings ever approach XpPerEvent × 2,
    // the leaderboard stops measuring participation and starts measuring screen
    // time.

    /// <summary>
    /// Master switch for the whole XP feature (accrual, the board, and the /xp
    /// commands). Default true; the board additionally needs XpBoardChannelId.
    /// </summary>
    public bool XpEnabled { get; set; } = true;

    /// <summary>
    /// Channel the auto-updating XP leaderboard is maintained in. 0 disables the
    /// board (accrual and /xp still work). This channel should be locked so only
    /// the bot can post — the board is edited in place and pinned, and any member
    /// message in here pushes it out of view until the next refresh.
    /// </summary>
    public ulong XpBoardChannelId { get; set; } = default;

    /// <summary>Whether the leaderboard board is maintained. Default true.</summary>
    public bool XpBoardEnabled { get; set; } = true;

    /// <summary>How often (minutes) the board is rebuilt. Default 10; floored to 2.</summary>
    public int XpBoardRefreshIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// How many members appear per leaderboard page. Default 25. The pinned board
    /// always shows page 1; the buttons under it open a PRIVATE paged view, so
    /// this is also the page size for /xp-leaderboard.
    /// </summary>
    public int XpBoardPageSize { get; set; } = 25;

    /// <summary>
    /// Draw members' Discord avatars on the rendered board. Default true. Turning
    /// this off skips all avatar fetching (one HTTP call per new avatar, cached
    /// by URL) and draws plain discs instead — faster, and one less thing that can
    /// fail, at the cost of a much less personal board.
    /// </summary>
    public bool XpBoardShowAvatars { get; set; } = true;

    /// <summary>
    /// Maintain the pinned "how to earn XP" guide directly above the leaderboard.
    /// Default true. The guide is posted FIRST so it sits above the board —
    /// Discord has no way to hold a message at the top of a channel, so the order
    /// comes purely from post order and the service repairs it if it inverts.
    /// </summary>
    public bool XpGuideEnabled { get; set; } = true;

    /// <summary>
    /// Hide members who have left the server from the board. Default true — a
    /// board topped by people who are gone reads as a memorial, not a contest.
    /// Their ledger rows and lifetime totals are kept either way.
    /// </summary>
    public bool XpBoardCurrentMembersOnly { get; set; } = true;

    /// <summary>
    /// Channel for level-up announcements. 0 disables them. Must NOT be
    /// XpBoardChannelId — level-up posts there would bury the board, so the
    /// service refuses and logs a warning if the two match.
    /// </summary>
    public ulong XpLevelUpAnnounceChannelId { get; set; } = default;

    /// <summary>
    /// DM a member privately EVERY time they gain a level. Default true.
    ///
    /// This is the counterpart to XpLevelUpAnnounceEveryLevels: the member gets
    /// the frequent signal (that's the feedback loop the ladder runs on), while
    /// the channel only sees the rare milestone. Best-effort — a member with DMs
    /// closed simply doesn't get it, which doubles as a self-service opt-out.
    /// </summary>
    public bool XpLevelUpDmEnabled { get; set; } = true;

    /// <summary>
    /// Announce a level-up only when the member crosses a multiple of this many
    /// levels. Default 10, so posts land at Level 10, 20, 30 and so on. Set to 1
    /// to announce every single level.
    ///
    /// ── Why the default isn't 1 ──
    /// Mid-season a level costs roughly 900–1,200 XP and an op is worth ~650 with
    /// bonuses, so an active member levels up about once per op. Across forty
    /// members that's ~80 pings a week, which turns a celebration into a tax on
    /// the channel. At 10 it's about three posts per member per season.
    /// </summary>
    public int XpLevelUpAnnounceEveryLevels { get; set; } = 10;

    /// <summary>
    /// Announce when a new member takes first place on the leaderboard. Default
    /// true. Posts to XpLevelUpAnnounceChannelId (and is skipped entirely if that
    /// is unset, or is the same channel as the board).
    ///
    /// A first sighting after startup, and the first cycle of a new season, both
    /// seed the tracked leader silently — otherwise every restart would crown
    /// whoever happens to be ahead, and every new season would crown someone on
    /// zero XP.
    /// </summary>
    public bool XpLeaderChangeAnnounceEnabled { get; set; } = true;

    /// <summary>
    /// Minimum gap between "new #1" announcements, in minutes. Default 60.
    /// Bounds the worst case where two members sit within a few XP of each other
    /// and trade the lead back and forth. The tracked leader still updates during
    /// the cooldown — only the post is suppressed. 0 disables the cooldown.
    /// </summary>
    public int XpLeaderChangeCooldownMinutes { get; set; } = 60;

    /// <summary>How often (minutes) XP is derived from activity tables. Default 10; floored to 1.</summary>
    public int XpAccrualIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// How many days back each accrual pass re-derives. Default 3. This is the
    /// self-healing window: anything missed while the bot was down is picked up
    /// as long as it happened within this many days. Raising it costs a little
    /// query time per cycle and nothing else — awards are idempotent.
    /// </summary>
    public int XpAccrualLookbackDays { get; set; } = 3;

    /// <summary>XP for attending a clan event. The dominant source — default 500.</summary>
    public int XpPerEvent { get; set; } = 500;

    /// <summary>XP for attending a clan meeting. Default 250.</summary>
    public int XpPerMeeting { get; set; } = 250;

    /// <summary>
    /// Bonus for RSVPing "Going" and then actually showing up. Default 50. Only
    /// ever additive — nobody is docked for missing an event they RSVP'd to,
    /// since a penalty would just train people to stop RSVPing.
    /// </summary>
    public int XpRsvpHonoredBonus { get; set; } = 50;

    /// <summary>Bonus every 3rd consecutive event attended. Default 150. 0 disables.</summary>
    public int XpStreak3Bonus { get; set; } = 150;

    /// <summary>Bonus every 5th consecutive event attended. Default 400. 0 disables.</summary>
    public int XpStreak5Bonus { get; set; } = 400;

    /// <summary>XP per completed 15 minutes in voice (outside events). Default 10. 0 disables voice XP.</summary>
    public int XpVoicePer15Minutes { get; set; } = 10;

    /// <summary>
    /// Maximum voice XP per member per UTC day. Default 60 (= 1.5h credited).
    /// This cap is what stops an idler from out-earning an attendee. 0 = uncapped
    /// (not recommended).
    /// </summary>
    public int XpVoiceDailyCap { get; set; } = 60;

    /// <summary>
    /// Voice sessions shorter than this many minutes earn nothing. Default 15 —
    /// kills the join/leave-repeatedly exploit.
    /// </summary>
    public int XpVoiceMinSessionMinutes { get; set; } = 15;

    /// <summary>
    /// Exclude the events category, the events VC and the meeting VC from voice
    /// XP. Default true: that time already pays as event/meeting attendance, and
    /// paying twice blurs the "one op ≈ seven hours of hanging out" ratio the
    /// economy is built on. The guild's AFK channel is always excluded regardless.
    /// </summary>
    public bool XpVoiceExcludeEventChannels { get; set; } = true;

    /// <summary>
    /// Extra voice channel IDs that earn no XP, comma-separated. For music/bot
    /// channels or anywhere else idling is expected.
    /// </summary>
    public string XpVoiceExcludedChannelIds { get; set; } = string.Empty;

    /// <summary>XP per chat message. Default 2 — deliberately trivial.</summary>
    public int XpPerMessage { get; set; } = 2;

    /// <summary>
    /// Messages that earn XP per member per UTC day. Default 10, so the chat
    /// ceiling is 20 XP/day. A full week of maxed chat is worth ~28% of one op.
    /// </summary>
    public int XpMessageDailyCap { get; set; } = 10;

    /// <summary>
    /// XP cost of the first level step (1→2). Default 150. The curve is linear:
    /// step(L) = XpLevelBase + XpLevelStep·(L−1).
    /// </summary>
    public int XpLevelBase { get; set; } = 150;

    /// <summary>
    /// How much each level step costs over the previous one. Default 40. Linear
    /// (not geometric) on purpose — a geometric curve makes late-season levels
    /// unreachable and the ladder stops motivating anyone once the leaders pull
    /// clear.
    /// </summary>
    public int XpLevelStep { get; set; } = 40;

    /// <summary>
    /// Level ceiling. Default 100 — far above a realistic season (an active
    /// member lands around Level 28 in a 13-week season), so it functions as a
    /// sanity bound rather than a goal.
    /// </summary>
    public int XpMaxLevel { get; set; } = 100;

    /// <summary>
    /// DEFAULT planned length in days for a new season, used when /xp-season start
    /// is run without a days: option. Default 30 (a one-month trial run).
    ///
    /// This is DISPLAY ONLY. It sets the "12 days to go" line on the board and
    /// nothing else — the season does not end when it elapses, and the board
    /// switches to "past its planned end" rather than pretending it closed. 0 =
    /// no target date at all.
    /// </summary>
    public int XpSeasonLengthDays { get; set; } = 30;

    /// <summary>
    /// Minimum rank to run the /xp-season subcommands. Default MAJ.
    /// Administrator or Manage Roles always passes.
    ///
    /// NOTE: this no longer governs /xp-adjust. Handing out XP by hand is the one
    /// XP command that can quietly rewrite the standings, so it is gated on an
    /// explicit role instead of a rank threshold. See <see cref="XpAdjustRoleId"/>.
    /// </summary>
    public string XpAdminMinRank { get; set; } = "MAJ";

    /// <summary>
    /// The role allowed to run /xp-adjust, the manual XP grant/deduction. Set to
    /// the HQ role.
    ///
    /// Deliberately a ROLE and not a rank threshold. Every other way of earning XP
    /// is derived from activity the bot already records and is auditable against it;
    /// /xp-adjust is the only one that writes a number nobody can check against
    /// anything, which makes it the only real way the leaderboard can be made to
    /// look rigged. That justifies a smaller, explicitly named group than "MAJ and
    /// above", and a group whose membership is changed by handing out a role rather
    /// than by a promotion.
    ///
    /// Its own key rather than a reuse of TicketHqRoleId or OfficerAppHqRoleId, in
    /// line with how PalworldAdminRoleId is kept separate: the ids happen to match
    /// today, and the two concerns should be able to diverge without one silently
    /// changing the other.
    ///
    /// Fails CLOSED. Set to 0 to fall back to the XpAdminMinRank rank gate; if it
    /// names a role that does not exist in the guild, nobody but an Administrator
    /// can adjust XP and the handler logs why.
    /// </summary>
    public ulong XpAdjustRoleId { get; set; } = 1408089365816541264;

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
    /// Parses XpVoiceExcludedChannelIds into channel IDs. Unparseable entries are
    /// skipped rather than throwing, so one typo can't stop voice XP accruing for
    /// the whole server.
    /// </summary>
    public List<ulong> GetXpVoiceExcludedChannelIdsList()
    {
        var result = new List<ulong>();
        if (string.IsNullOrWhiteSpace(XpVoiceExcludedChannelIds)) return result;

        foreach (var raw in XpVoiceExcludedChannelIds.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(raw, out var id) && id != 0) result.Add(id);
        }

        return result;
    }

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
    /// Logic: a member gets the regular WindowDays only if they hold a rank
    /// above the short-window tier, meaning any RankRoles entry that is NOT
    /// also listed in ShortWindowRoles (PVT and above). Everyone else gets
    /// ShortWindowDays. That covers three cases:
    ///
    /// 1. Guest or RCT only: ShortWindowDays, as before.
    /// 2. No rank role at all: ShortWindowDays. These are people who joined
    ///    and never picked up a rank, so they are held to the same short
    ///    window as a recruit rather than the full member window.
    /// 3. Guest or RCT plus a higher rank (the Guest role was not stripped on
    ///    promotion): the higher rank wins and they get WindowDays.
    ///
    /// Exempt roles (Moderator, Reserve and anything else in
    /// GetExemptRolesList) are filtered out before this is reached, so an
    /// unranked exempt member is never judged on the short window.
    /// </summary>
    public int GetWindowDaysForRoles(IEnumerable<string> memberRoleNames)
    {
        var roleList = memberRoleNames.ToList();
        var shortRoles = GetShortWindowRolesList();
        var rankRoles = GetRankRolesList();

        // Any rank role that is not itself a short-window role, e.g. PVT and up.
        var hasHigherRank = roleList.Any(r =>
            rankRoles.Contains(r, StringComparer.OrdinalIgnoreCase)
            && !shortRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        return hasHigherRank ? WindowDays : ShortWindowDays;
    }
}