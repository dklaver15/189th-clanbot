using System.Text.RegularExpressions;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Filter that decides whether a Reddit post is worth surfacing to the
/// leads channel. Designed to be tightenable without touching the
/// service: keywords live as static arrays here, thresholds (none
/// currently) would live in RedditLeadsOptions.
///
/// ── Tier 1: positive keyword match ──
/// At least one of the LFG-signal phrases must appear in the title or
/// body. Word-boundary regex (case-insensitive) so "lfg" doesn't match
/// "selfgoverning" but does match "Lfg" / "LFG!". Multi-word phrases
/// match across whitespace runs.
///
/// ── Tier 2: negative keyword reject ──
/// Posts containing any of the recruiter-side phrases ("recruiting",
/// "join our", etc.) are rejected outright — these are competing clans
/// posting, not leads. The reject list runs after the positive match;
/// a post can mention "lfg" in passing while still being a recruitment
/// ad ("we're recruiting LFG-style players"), and the negative list
/// catches that.
///
/// ── No tier 3 (author quality floor) ──
/// The previous OAuth-API version had an author age + karma floor as
/// tier 3. Reddit's public RSS feed doesn't expose either field, and
/// new Data API app creation is closed off for non-moderation cases,
/// so we run without it. The keyword filter does the bulk of the work;
/// throwaway-account noise is handled by officers hitting Skip.
/// Stickied / NSFW post filtering also moved here from the OAuth
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
/// for future analysis but the matcher no longer gates on it. If a
/// link-post filter is ever needed again, build it against real link-
/// post markup (not the [link] footer) and validate against a live
/// audit script before re-enabling.
/// </summary>
public static class LeadMatcher
{
    private static readonly string[] PositiveKeywords = new[]
    {
        "lfg",
        "looking for clan",
        "looking for a clan",
        "looking for group",
        "looking for a group",
        "need a clan",
        "find a clan",
        "bf6 clan",
        "battlefield 6 clan",
        "battlefield clan",
        "join a clan",
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
    /// Run both keyword tiers. Returns the comma-separated list of
    /// triggered positive keywords on a pass, or a human-readable
    /// RejectReason on a miss (logged at Information level by the
    /// service so we can audit "why didn't this lead come through?"
    /// without re-fetching from Reddit).
    /// </summary>
    public static TextMatchResult MatchText(RedditPost post)
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

        return new(true, string.Join(", ", positiveMatches), null);
    }
}