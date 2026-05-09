using System.Text.RegularExpressions;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Three-tier filter that decides whether a Reddit post is worth surfacing
/// to the leads channel. Designed to be tightenable without touching the
/// service: keywords live as static arrays here, thresholds live in
/// RedditLeadsOptions.
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
/// ── Tier 3: quality floor ──
/// Account age and karma checks happen against a fetched user profile.
/// Stickied / NSFW posts are also dropped at this tier — stickied posts
/// are usually mod announcements and NSFW is never in scope for our
/// channel.
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
    /// Result of running a post through the text-side filters (tiers 1 and 2).
    /// Author-side filtering happens separately because it requires an extra
    /// API call we want to avoid for posts that won't pass text filtering anyway.
    /// </summary>
    public sealed record TextMatchResult(bool Passes, string MatchedKeywords, string? RejectReason);

    /// <summary>
    /// Run tiers 1 + 2. Cheap, no network. Returns the comma-separated list
    /// of triggered positive keywords on a pass, or a human-readable
    /// RejectReason on a miss (used in trace-level logs to debug "why did
    /// this post not surface?" without re-fetching from Reddit).
    /// </summary>
    public static TextMatchResult MatchText(RedditPost post)
    {
        if (post.Stickied)         return new(false, "", "stickied");
        if (post.Over18)           return new(false, "", "over_18");

        // We deliberately don't filter on is_self. Link posts (e.g. "BF6 LFG —
        // here's my gameplay clip") can still be legitimate leads if the title
        // carries the LFG signal. The keyword filter and author quality floor
        // are doing the real work; over-strict pre-filtering risks dropping
        // good leads.

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

    /// <summary>
    /// Tier 3. Returns null if the author passes the floor, otherwise a
    /// short reason string. A null author (Reddit /about returned 404)
    /// is rejected as "deleted" — we can't verify the floor and the
    /// signal-to-noise on those isn't worth the risk.
    /// </summary>
    public static string? RejectAuthor(RedditUser? author, RedditLeadsOptions options)
    {
        if (author is null) return "author profile missing";
        if (author.AccountAgeDays < options.MinAccountAgeDays)
            return $"account age {author.AccountAgeDays}d < {options.MinAccountAgeDays}d";
        if (author.TotalKarma < options.MinKarma)
            return $"karma {author.TotalKarma} < {options.MinKarma}";
        return null;
    }
}
