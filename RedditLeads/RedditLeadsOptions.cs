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

    /// <summary>How often the polling loop wakes. Default 60 min.</summary>
    public int PollingIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Seconds to wait between subreddit requests within a cycle. Reddit now
    /// rate-limits anonymous RSS from datacenter IPs hard — observed limit is
    /// roughly ONE request per ~40s, with back-to-back requests returning 429.
    /// Firing all subs back-to-back means only the first one succeeds and the
    /// rest are silently 429'd (which is how leads dried up). At the default 45s,
    /// 35 subs take ~26 min — comfortably inside a 60-min cycle — and every sub
    /// gets polled successfully. Lower only if Reddit relaxes; raise if 429s
    /// persist in the cycle summary.
    /// </summary>
    public int RequestSpacingSeconds { get; set; } = 45;

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

    /// <summary>
    /// Maximum number of leads surfaced (DB row + embed posted) per
    /// polling cycle. Sized to recruiter capacity — if officers can
    /// only follow up on N leads per cycle, surfacing more just buries
    /// the queue.
    ///
    /// ── Per-cycle, not per-hour ──
    /// Tied to PollingIntervalMinutes by definition. At the default
    /// 60-min interval, "per cycle" == "per hour". If PollingIntervalMinutes
    /// is changed, recompute the intended hourly rate before adjusting
    /// this value (e.g. interval=30min + cap=10 → 20 leads/hour).
    ///
    /// ── Overflow behavior ──
    /// When the cap is hit mid-cycle, remaining subs and remaining
    /// posts within the current sub are skipped — no DB insert, no
    /// embed. Those posts stay in Reddit's /new.rss feed and remain
    /// inside the MaxPostAgeHours window, so the next cycle will
    /// re-discover them and re-evaluate. No "queued" / "deferred"
    /// state is persisted. Sustained floods past 24h naturally age out
    /// without surfacing, which is the desired behavior — leads
    /// recruiters couldn't get to are leads they wouldn't have worked.
    ///
    /// ── Sub ordering ──
    /// Subs are processed in the order they appear in Subreddits, so
    /// if a high-volume sub is first and eats the cap, later subs get
    /// nothing that cycle. In practice cross-sub match volume usually
    /// stays well under the cap; if that stops being true, switch
    /// RunCycleAsync to round-robin or sort-by-newest.
    /// </summary>
    public int MaxLeadsPerCycle { get; set; } = 10;

    /// <summary>
    /// Comma-separated list of subreddit names (no "r/" prefix) where the
    /// LFG keyword filter ALONE isn't strict enough — posts from these
    /// subs must additionally mention one of the games we play (per
    /// <see cref="GameAliases"/>) to be surfaced. Intended
    /// for broad LFG aggregators like r/GamerPals, r/gamerlfg,
    /// r/LookingForGroup where the LFG signal is real but the game
    /// usually isn't ours.
    ///
    /// Subs in this list MUST also appear in Subreddits — being here on
    /// its own does nothing. Subs NOT in this list pass with the standard
    /// positive/negative keyword pipeline only.
    ///
    /// Empty (default) disables the game-filter feature entirely.
    /// </summary>
    public string GameFilteredSubs { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated Reddit-vernacular names for the games we play —
    /// the vocabulary behind the <see cref="GameFilteredSubs"/> filter.
    /// Acronyms welcome ("bf6", "hd2", "hll"): Reddit posters write those
    /// where a game's official title never appears.
    ///
    /// Word-boundary regex applies, so short aliases like "hd2" won't
    /// match "shd24"; multi-word entries match across flexible whitespace
    /// ("hell  let  loose" -> match).
    ///
    /// Empty while GameFilteredSubs is set is a misconfiguration —
    /// LeadMatcher warns and skips the filter rather than rejecting every
    /// post on those subs.
    /// </summary>
    public string GameAliases { get; set; } = string.Empty;

    public List<string> GetSubredditsList() =>
        Subreddits
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public List<string> GetGameFilteredSubsList() =>
        GameFilteredSubs
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    public List<string> GetGameAliasesList() =>
        GameAliases
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
}