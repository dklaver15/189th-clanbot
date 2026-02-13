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

    /// <summary>Minimum messages required within the window to stay active.</summary>
    public int MinMessages { get; set; } = 5;

    /// <summary>Minimum voice hours required within the window to stay active.</summary>
    public double MinVoiceHours { get; set; } = 1.0;

    /// <summary>Days after AWOL assignment before HQ is notified.</summary>
    public int AwolGraceDays { get; set; } = 2;

    /// <summary>How often (in minutes) the background check runs.</summary>
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Comma-separated list of role names that are exempt from AWOL tracking.
    /// Officers, admins, bots, etc.
    /// </summary>
    public string ExemptRoles { get; set; } = "Admin,Moderator,Officer,Bot";

    public List<string> GetExemptRolesList() =>
        ExemptRoles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
