namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Strongly-typed view of the "RedditLeads" configuration section.
/// Validated on startup via RedditLeadsServiceCollectionExtensions —
/// missing required fields fail the bot fast rather than silently
/// running a broken polling loop.
///
/// Secrets (ClientSecret, Password) are populated from environment
/// variables in production via the standard ASP.NET Core config
/// double-underscore mapping, e.g. RedditLeads__ClientSecret. The
/// values committed to appsettings.json are placeholders.
/// </summary>
public sealed class RedditLeadsOptions
{
    public const string Section = "RedditLeads";

    /// <summary>Master switch. When false, the polling service never starts.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the polling loop wakes. Default 10 min.</summary>
    public int PollingIntervalMinutes { get; set; } = 10;

    // ── Reddit OAuth credentials ─────────────────────────────────────

    /// <summary>Client ID from the Reddit app (under the app name in /prefs/apps).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Client secret from the Reddit app. Populated from RedditLeads__ClientSecret env var.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Reddit username of the dedicated bot account (e.g. "189th_recruiter_bot").</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Reddit password of the bot account. Populated from RedditLeads__Password env var.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Reddit User-Agent string. Required by Reddit's API rules — must
    /// identify the application and the operator. Format Reddit prefers:
    /// "platform:appname:version (by /u/yourusername)".
    /// </summary>
    public string UserAgent { get; set; } = "ClanGuard/1.0 by xAP3XRONINx";

    // ── Discord posting target ───────────────────────────────────────

    /// <summary>Channel ID where lead embeds are posted. Discord perms gate visibility.</summary>
    public ulong LeadsChannelId { get; set; }

    // ── Subreddit + matching tuning ──────────────────────────────────

    /// <summary>Comma-separated list of subreddits (no "r/" prefix), e.g. "Battlefield,Battlefield6,FindAClan".</summary>
    public string Subreddits { get; set; } = string.Empty;

    /// <summary>
    /// Posts whose author has a Reddit account younger than this are
    /// silently skipped. Filters out throwaway/spam accounts.
    /// </summary>
    public int MinAccountAgeDays { get; set; } = 30;

    /// <summary>
    /// Posts whose author has less total karma than this are silently
    /// skipped. Same throwaway filter.
    /// </summary>
    public int MinKarma { get; set; } = 50;

    /// <summary>
    /// Posts older than this when first observed are skipped. Bounds the
    /// damage on cold start (no flood of stale posts after a long outage)
    /// and keeps officers focused on actionable, fresh leads.
    /// </summary>
    public int MaxPostAgeHours { get; set; } = 24;

    // ── Helpers ──────────────────────────────────────────────────────

    public List<string> GetSubredditsList() =>
        Subreddits
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}
