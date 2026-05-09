namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Strongly-typed view of the "RedditLeads" configuration section.
/// Validated on startup via RedditLeadsServiceCollectionExtensions —
/// missing required fields fail the bot fast rather than silently
/// running a broken polling loop.
///
/// ── No credentials needed ──
/// The lead service consumes Reddit's public RSS feed at
/// /r/{sub}/new.rss, which requires no authentication. The previous
/// OAuth-API version had ClientId/ClientSecret/Username/Password
/// fields here; those were removed in the RSS migration when Reddit
/// closed off new Data API app creation for non-moderation use cases.
/// </summary>
public sealed class RedditLeadsOptions
{
    public const string Section = "RedditLeads";

    /// <summary>Master switch. When false, the polling service never starts.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the polling loop wakes. Default 10 min.</summary>
    public int PollingIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// HTTP User-Agent string. Reddit's edge network blocks generic UAs
    /// (curl, python-requests, empty). Format Reddit prefers:
    /// "platform:appname:version (by /u/yourusername)". This is set on
    /// the typed HttpClient at registration time; the polling service
    /// inherits it on every request.
    /// </summary>
    public string UserAgent { get; set; } = "ClanGuard/1.0 by xAP3XRONINx";

    /// <summary>Channel ID where lead embeds are posted. Discord perms gate visibility.</summary>
    public ulong LeadsChannelId { get; set; }

    /// <summary>Comma-separated list of subreddits (no "r/" prefix), e.g. "Battlefield,Battlefield6,FindAClan".</summary>
    public string Subreddits { get; set; } = string.Empty;

    /// <summary>
    /// Posts older than this when first observed are skipped. Bounds the
    /// damage on cold start (no flood of stale posts after a long outage)
    /// and keeps officers focused on actionable, fresh leads.
    /// </summary>
    public int MaxPostAgeHours { get; set; } = 24;

    public List<string> GetSubredditsList() =>
        Subreddits
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}