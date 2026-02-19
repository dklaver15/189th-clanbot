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
    public string ExemptRoles { get; set; } = "Admin,Moderator,Retired,Bot";
    
    /// <summary>Path to the Google service account credentials JSON file.</summary>
    public string GoogleCredentialsPath { get; set; } = "google-credentials.json";

    /// <summary>The Google Spreadsheet ID (from the sheet URL).</summary>
    public string GoogleSpreadsheetId { get; set; } = string.Empty;

    /// <summary>The sheet/tab name to write gamertags to.</summary>
    public string GoogleSheetName { get; set; } = "Gamertags";

    /// <summary>The sheet/tab name for the nightly roster export.</summary>
    public string RosterSheetName { get; set; } = "Roster";

    /// <summary>Name of the voice channel to track for "Last Events" column.</summary>
    public string EventsVoiceChannelName { get; set; } = "Events";

    /// <summary>Hour of day (UTC, 0-23) to run the nightly roster export.</summary>
    public int RosterExportHourUtc { get; set; } = 6;

    /// <summary>
    /// Comma-separated list of rank role names in order from lowest to highest.
    /// Used to identify a user's current rank and track time-in-rank.
    /// </summary>
    public string RankRoles { get; set; } = "RCT,PVT,PFC,SPC,CPL,SGT,SSG,SFC,MSG,1SG,SGM,CSM,SMA,2ndLT,1stLT,CPT,MAJ,LTC,COL,BG,MG,LTG,GEN,GoA";

    public List<string> GetExemptRolesList() =>
        ExemptRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    
    public List<string> GetShortWindowRolesList() =>
        ShortWindowRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public List<string> GetRankRolesList() =>
        RankRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    
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