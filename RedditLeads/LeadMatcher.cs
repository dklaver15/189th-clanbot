using System.Text.RegularExpressions;
using ClanGuardBot.PatrolWatch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Filter that decides whether a Reddit post is worth surfacing to the
/// leads channel. Designed to be tightenable without touching the
/// service: positive/negative keywords live as static arrays here, the
/// game-filter vocabulary is config-driven from appsettings.
///
/// ── Tier 1: positive keyword match ──
/// At least one of the LFG-signal phrases must appear in the title or
/// body. Word-boundary regex (case-insensitive) so "lfg" doesn't match
/// "selfgoverning" but does match "Lfg" / "LFG!". Multi-word phrases
/// match across whitespace runs.
///
/// The positive list is intentionally WIDE — it catches both structured-
/// group asks ("looking for clan", "find a clan") and casual-friend
/// asks ("looking for gaming buddy", "seeking pals", "lf duo"). The
/// reasoning: 189th is a structured clan but the cost of an unseen
/// real lead is much higher than the cost of an officer hitting Skip
/// on a casual-friend post that isn't the right fit. Officers can
/// dismiss a false positive in two seconds. Calibrate by reading
/// rejection logs (`grep "reason=no positive"`) — anything in there
/// that looks like a real recruitment ask is a missing positive
/// keyword. Tightening the list later is a one-line change.
///
/// ── Tier 2: negative keyword reject ──
/// Posts containing any of the recruiter-side phrases ("recruiting",
/// "join our", etc.) are rejected outright — these are competing clans
/// posting, not leads. The reject list runs after the positive match;
/// a post can mention "lfg" in passing while still being a recruitment
/// ad ("we're recruiting LFG-style players"), and the negative list
/// catches that.
///
/// ── Tier 3: per-sub game filter ──
/// Some subs (currently just r/GamerPals) are broad LFG aggregators
/// covering every game under the sun. The tier-1 positive keywords
/// pass casual-friend asks regardless of game, so without an extra
/// gate the leads channel fills with posts for games we don't play.
///
/// Configuration lives in two places:
/// • RedditLeads:GameFilteredSubs (comma-separated sub names) — which
///   subs the filter applies to.
/// • PatrolWatch:MatchedGames[].RedditAliases — the vocabulary, owned
///   by each game's config entry so adding a game is one block, not
///   two. We pull from PatrolWatch's MatchedGames because that's the
///   canonical "games we play" record; the Reddit-vernacular aliases
///   live there as a separate field from the Discord-activity
///   substrings because the vocabularies are different.
///
/// All filtered subs share the same flattened alias regex — there's
/// no use case yet for different game lists on different subs (broad
/// LFG aggregators all want the same "any game we play" gate). If
/// that changes, swap the shared regex for per-sub regexes here.
///
/// Subs NOT in GameFilteredSubs pass with the standard tier-1/tier-2
/// pipeline only — behavior unchanged from before this tier existed.
///
/// The matched game is appended to MatchedKeywords so it surfaces in
/// the embed and officers can see at a glance why a filtered-sub post
/// made it through.
///
/// ── No author quality floor ──
/// The previous OAuth-API version had an author age + karma floor.
/// Reddit's public RSS feed doesn't expose either field, and new Data
/// API app creation is closed off for non-moderation cases, so we run
/// without it. The keyword filter does the bulk of the work; throwaway-
/// account noise is handled by officers hitting Skip. Stickied / NSFW
/// post filtering also moved to the keyword filter from the OAuth
/// version — RSS doesn't expose those flags either.
///
/// ── Why there's no IsSelf gate ──
/// An earlier iteration rejected link posts up-front via post.IsSelf,
/// on the theory that link entries on aggregator subs were directory-
/// style noise (the linked sub's sidebar text leaking into the body).
/// Validation against live RSS proved the heuristic was a false
/// positive: Reddit appends a "[link]" footer to EVERY entry's RSS
/// body — it's the permalink anchor, not a link-post signal — so the
/// IsSelf detector classified ~100% of real LFG posts on healthy subs
/// as link posts and silently dropped them. The original problem the
/// gate was solving (directory-entry noise from r/PS5LFG, r/PCGameLFG,
/// r/RecruitLTG) was actually caused by those subs being deleted /
/// renamed and Reddit 302-redirecting the RSS feed to a search-results
/// stream of OTHER subs. That's now handled upstream by RedditRssClient's
/// dead-sub redirect detection. IsSelf is still populated on RedditPost
/// for future analysis but the matcher no longer gates on it.
///
/// ── Singleton lifecycle ──
/// Registered as a singleton in RedditLeadsServiceCollectionExtensions
/// because the per-sub regex is built once from config at startup and
/// is immutable thereafter. Thread-safe by construction (Regex
/// instances and the readonly dictionary).
/// </summary>
public sealed class LeadMatcher
{
    private static readonly string[] PositiveKeywords = new[]
    {
        // ── Structured-group asks ──
        // Original clan-focused phrasings. Cleanest signal that a poster
        // is open to joining an organized community rather than just
        // finding session partners.
        "lfg",
        "looking for clan",
        "looking for a clan",
        "looking for group",
        "looking for a group",
        "looking for community",
        "looking for a community",
        "need a clan",
        "find a clan",
        "join a clan",
        "join a community",
        "bf6 clan",
        "battlefield 6 clan",
        "battlefield clan",

        // ── "looking for + people-noun" patterns ──
        // Casual-friend phrasings dominant on r/GamerPals, r/LookingForGroup,
        // r/FindAClan. These are not "I want a clan" asks but the audience
        // overlap is real — many casual-friend posters do convert when
        // pitched a structured group with active voice / events.
        "looking for friends",
        "looking for a friend",
        "looking for gaming",
        "looking for buddies",
        "looking for a buddy",
        "looking for pals",
        "looking for a pal",
        "looking for someone",
        "looking for people",
        "looking for a duo",
        "looking for a squad",
        "looking for a team",
        "looking for a partner",
        "looking for chill",
        "looking to play",

        // ── "LF + people-noun" patterns ──
        // Standalone "LF" abbreviation followed by a recruitment noun.
        // We deliberately do NOT include bare "lf" — it triggers on
        // single-session asks like "LF DPS for mythic raid" that aren't
        // clan-recruitment relevant.
        "lf friends",
        "lf a friend",
        "lf gaming",
        "lf duo",
        "lf squad",
        "lf team",
        "lf people",
        "lf someone",
        "lf community",
        "lf buddy",
        "lf buddies",
        "lf partner",
        "lf pals",

        // ── "seeking + people-noun" patterns ──
        "seeking pals",
        "seeking friends",
        "seeking a buddy",
        "seeking a partner",
        "seeking gaming",
        "seeking people",

        // ── Bare gaming-friend phrases ──
        // Catch posts where the recruitment ask is phrased without a
        // "looking for" / "lf" / "seeking" prefix, e.g. "Smite Friends"
        // or "Coop partner needed".
        "gaming friends",
        "gaming buddy",
        "gaming buddies",
        "gaming pals",
        "gaming partner",
        "co-op partner",
        "coop partner",
    };

    private static readonly string[] NegativeKeywords = new[]
    {
        "we are recruiting",
        "now recruiting",
        "currently recruiting",
        "we're recruiting",
        "join our clan",
        "join our discord",
        "our clan is",
        "our clan was",
        "our community",
        "[h]",
        "[recruiting]",
        "now hiring",
        "we offer",
        "weekly events",
    };

    // Pre-compiled regexes built once at class-load. Word-boundary anchors
    // on both sides keep "lfg" from matching "selfgoverning" while still
    // allowing case- and punctuation-tolerant hits like "LFG!".
    private static readonly Regex PositivePattern = BuildPattern(PositiveKeywords);
    private static readonly Regex NegativePattern = BuildPattern(NegativeKeywords);

    /// <summary>
    /// Sub-name → shared game regex. Built once from config in the
    /// constructor. Empty when GameFilteredSubs is unset or no
    /// RedditAliases are configured — tier 3 becomes a no-op in that
    /// case, which is the disabled-feature state.
    /// </summary>
    private readonly Dictionary<string, Regex> _subGameFilters;

    public LeadMatcher(
        IOptions<RedditLeadsOptions> redditOptions,
        IOptions<PatrolWatchOptions> patrolOptions,
        ILogger<LeadMatcher> logger)
    {
        var filteredSubs = redditOptions.Value.GetGameFilteredSubsList();

        // Flatten every game's RedditAliases into one keyword list. Distinct
        // case-insensitive so duplicates across games don't bloat the regex.
        var aliases = patrolOptions.Value.MatchedGames
            .SelectMany(g => g.RedditAliases ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (filteredSubs.Count == 0)
        {
            // Feature off — no subs configured. Don't warn; this is a
            // valid steady state (e.g. when r/GamerPals isn't being
            // polled in the current rotation).
            _subGameFilters = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        if (aliases.Length == 0)
        {
            // Subs configured but no aliases anywhere — would produce a
            // filter that rejects every post on those subs. Almost
            // certainly a misconfiguration; warn loudly and leave the
            // filter empty so the affected subs fall back to standard
            // tier-1/tier-2 behavior rather than going silent.
            logger.LogWarning(
                "RedditLeads:GameFilteredSubs is set ({Subs}) but PatrolWatch:MatchedGames has no RedditAliases. " +
                "Game filter will be skipped — these subs will pass on tier 1+2 alone.",
                string.Join(", ", filteredSubs));
            _subGameFilters = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        var sharedPattern = BuildPattern(aliases);
        _subGameFilters = filteredSubs.ToDictionary(
            s => s,
            _ => sharedPattern,
            StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "LeadMatcher game filter active for {SubCount} sub(s) [{Subs}] with {AliasCount} game alias(es)",
            filteredSubs.Count, string.Join(", ", filteredSubs), aliases.Length);
    }

    private static Regex BuildPattern(string[] phrases)
    {
        // Allow flexible whitespace inside multi-word phrases (e.g. "looking  for clan" or
        // "looking-for-clan") by replacing literal spaces with \s+ post-escape.
        var alternation = string.Join("|",
            phrases
                .Select(Regex.Escape)
                .Select(p => p.Replace(@"\ ", @"\s+")));

        return new Regex(
            $@"\b(?:{alternation})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    /// Result of running a post through the text-side filters.
    /// </summary>
    public sealed record TextMatchResult(bool Passes, string MatchedKeywords, string? RejectReason);

    /// <summary>
    /// Run all keyword tiers. Returns the comma-separated list of
    /// triggered positive keywords (plus matched game on filtered subs)
    /// on a pass, or a human-readable RejectReason on a miss (logged at
    /// Information level by the service so we can audit "why didn't
    /// this lead come through?" without re-fetching from Reddit).
    /// </summary>
    public TextMatchResult MatchText(RedditPost post)
    {
        var haystack = string.Concat(post.Title, "\n", post.Selftext);

        var positiveMatches = PositivePattern
            .Matches(haystack)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .ToList();

        if (positiveMatches.Count == 0)
            return new(false, "", "no positive keyword");

        var negativeHit = NegativePattern.Match(haystack);
        if (negativeHit.Success)
            return new(false, "", $"negative keyword: {negativeHit.Value}");

        // Tier 3: per-sub game filter. Only runs if the post's sub is
        // registered in _subGameFilters; otherwise behavior is unchanged
        // from the pre-tier-3 days.
        if (_subGameFilters.TryGetValue(post.Subreddit, out var gamePattern))
        {
            var gameMatches = gamePattern
                .Matches(haystack)
                .Select(m => m.Value.ToLowerInvariant())
                .Distinct()
                .ToList();

            if (gameMatches.Count == 0)
                return new(false, "", "no game keyword");

            // Surface both the LFG signal and the matched game so the
            // embed footer tells the officer not just "this is an LFG
            // post" but "this is an LFG post for a game we play".
            var combined = positiveMatches.Concat(gameMatches).Distinct();
            return new(true, string.Join(", ", combined), null);
        }

        return new(true, string.Join(", ", positiveMatches), null);
    }
}