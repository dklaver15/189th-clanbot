using System.Text.RegularExpressions;

namespace ClanGuardBot.Services;

/// <summary>
/// Decides whether two poll questions are asking the same thing. Cheap, local,
/// no model call.
///
/// ── Why this exists ──
/// The same question gets posted twice more often than you'd think: someone asks
/// in a game channel, then re-asks in #polls to reach everyone, or reruns
/// <c>/poll</c> after a typo. Both polls are real and both should stay, but at
/// their midpoint each one fires its own "still open" nudge and the general
/// channel gets two near-identical embeds minutes apart, which reads as a bot
/// bug. <see cref="PollReminderService"/> uses this to fold those into a single
/// reminder listing both polls.
///
/// ── How the comparison works ──
/// Questions are lowercased, split into words, stripped of function words
/// ("the", "would", "be", "in"…) and crudely singularised, then compared with the
/// Sørensen–Dice coefficient over the remaining content words. Word-set overlap
/// rather than edit distance on purpose: the duplicate is usually a reworded
/// opener ("are people interested in another X" vs "would anyone be interested in
/// a X"), which edit distance scores as very different while the content words
/// (practice, ranked, event, season) match almost exactly.
///
/// Only function words are treated as noise, never topic words: dropping
/// "interested" or "people" would make far too many unrelated questions look
/// alike. <see cref="MinSharedWords"/> then blocks the degenerate case where two
/// very short questions share one word and score 1.0.
///
/// Deliberately conservative: a missed grouping costs one extra reminder, a
/// false grouping hides a real poll from the people who'd have voted in it.
/// </summary>
public static class PollSimilarity
{
    /// <summary>
    /// Dice score at or above which two questions count as the same ask.
    ///
    /// <para>0.80, checked against real 189th phrasings. The reask that prompted
    /// this ("are people interested in another 'practice for ranked' event…" vs
    /// "would anyone be interested in a 'practice for ranked' event…") scores
    /// 0.88, while same-shape-different-subject pairs land at or below 0.75.
    /// "do we want a BF6 tournament?" vs "…a BF6 tournament next month?" is 0.75
    /// and stays separate, which is the right call: the extra words ARE the
    /// question.</para>
    /// </summary>
    public const double DefaultThreshold = 0.80;

    /// <summary>
    /// Floor on shared content words, so "wipe saturday?" and "wipe sunday?"
    /// can't group on a single word. Relaxed for questions that have fewer content
    /// words than this to begin with.
    /// </summary>
    private const int MinSharedWords = 3;

    private static readonly Regex WordToken = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);

    /// <summary>
    /// English function words: articles, auxiliaries, pronouns, prepositions,
    /// conjunctions and the handful of filler adverbs that show up in poll
    /// phrasing. Topic words are NOT listed here, on purpose (see the type docs).
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "then", "than", "so", "as",
        "of", "to", "in", "on", "at", "by", "for", "from", "with", "about", "into",
        "up", "out", "off", "over", "under", "again", "back",
        "i", "me", "my", "we", "us", "our", "you", "your", "yours", "it", "its",
        "he", "him", "his", "she", "her", "they", "them", "their",
        "this", "that", "these", "those", "there", "here",
        // Interrogatives: shared question shape says nothing about shared subject.
        // Without these, "which map should we play friday?" and "…saturday?" score
        // as duplicates on the strength of "which".
        "who", "whom", "whose", "which", "what", "when", "where", "why", "how",
        "is", "am", "are", "was", "were", "be", "been", "being",
        "do", "does", "did", "doing", "done",
        "have", "has", "had", "having",
        "will", "would", "shall", "should", "can", "could", "may", "might", "must",
        "get", "got", "gonna", "wanna",
        "not", "no", "yes", "just", "very", "really", "too", "also", "still",
        "any", "some", "all", "both", "each", "other", "another", "same",
        "more", "most", "much", "many", "one", "ok", "okay", "please", "thanks",
        "like", "ever", "even", "yet", "now", "already", "maybe",
    };

    /// <summary>
    /// True when both questions are asking the same thing. Null/blank or
    /// all-function-word questions never match, since there's nothing to compare.
    /// </summary>
    public static bool AreNearDuplicates(string? a, string? b, double threshold = DefaultThreshold)
        => Score(a, b) >= threshold;

    /// <summary>
    /// Dice coefficient over content words, 0..1. Returns 0 when either side has
    /// no content words or the pair fails the <see cref="MinSharedWords"/> floor,
    /// so callers can treat "0" as "not comparable" and "1" as "identical ask".
    /// Exposed separately from <see cref="AreNearDuplicates"/> so a caller can log
    /// how close a near-miss was.
    /// </summary>
    public static double Score(string? a, string? b)
    {
        var left  = ContentWords(a);
        var right = ContentWords(b);
        if (left.Count == 0 || right.Count == 0) return 0;

        var shared = left.Count(right.Contains);
        if (shared == 0) return 0;

        // Short questions get a proportionally lower floor rather than being
        // excluded outright, since "wipe saturday?" is a legitimate poll.
        var floor = Math.Min(MinSharedWords, Math.Min(left.Count, right.Count));
        if (shared < floor) return 0;

        return 2.0 * shared / (left.Count + right.Count);
    }

    /// <summary>
    /// The comparable words of a question: lowercased, punctuation dropped,
    /// function words removed, plural "s" trimmed so "events" matches "event".
    /// </summary>
    private static HashSet<string> ContentWords(string? question)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(question)) return set;

        foreach (Match m in WordToken.Matches(question.ToLowerInvariant()))
        {
            var word = m.Value.Trim('\'');
            if (word.Length == 0 || Noise.Contains(word)) continue;
            set.Add(Singularize(word));
        }

        return set;
    }

    /// <summary>
    /// Crude plural trim, enough for "events"/"event" and "maps"/"map" without
    /// dragging in a stemmer. Leaves "ss" endings and very short words alone.
    /// </summary>
    private static string Singularize(string word) =>
        word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal)
            ? word[..^1]
            : word;
}
