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
    /// Discord channel ID of the events voice channel. Used for attendance
    /// credit in auto-promotion (SGT+) — immune to renames.
    /// </summary>
    public ulong EventsVoiceChannelId { get; set; } = default;

    /// <summary>Hour of day (UTC, 0-23) to run the nightly roster export.</summary>
    public int RosterExportHourUtc { get; set; } = 6;

    /// <summary>Rolling window in days for the roster export activity stats. Independent of AWOL window.</summary>
    public int RosterWindowDays { get; set; } = 14;

    /// <summary>
    /// Comma-separated list of rank role names in order from lowest to highest.
    /// Used to identify a user's current rank and track time-in-rank.
    /// </summary>
    public string RankRoles { get; set; } = "RCT,PVT,PFC,SPC,CPL,SGT,SSG,SFC,MSG,1SG,SGM,CSM,SMA,2ndLT,1stLT,CPT,MAJ,LTC,COL,BG,MG,LTG,GEN,GoA";

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
    /// The minimum rank required to use the /comp-event command.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string CompEventMinRank { get; set; } = "CPT";

    /// <summary>
    /// The minimum rank required to use the /promote and /demote commands.
    /// Must exactly match one of the rank names in RankRoles (case-insensitive).
    /// </summary>
    public string PromoteDemoteMinRank { get; set; } = "2ndLT";

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

    // ─── Helpers ─────────────────────────────────────────────────────

    public List<string> GetExemptRolesList() =>
        ExemptRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetShortWindowRolesList() =>
        ShortWindowRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetRankRolesList() =>
        RankRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetPlatoonRolesList() =>
        PlatoonRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetAutoPromotionDryRunRanksList() =>
        AutoPromotionDryRunRanks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Returns the appropriate window days for a guild member based on their roles.
    /// Members with any role in ShortWindowRoles get ShortWindowDays; others get WindowDays.
    /// </summary>
    public int GetWindowDaysForRoles(IEnumerable<string> memberRoleNames)
    {
        var shortRoles = GetShortWindowRolesList();
        return memberRoleNames.Any(r => shortRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
            ? ShortWindowDays
            : WindowDays;
    }
}