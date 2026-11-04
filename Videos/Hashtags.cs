using System.Text.RegularExpressions;

namespace ClanGuardBot.Services;

/// <summary>
/// Hashtags from the upload form's Hashtags box. Whatever is typed is cleaned,
/// never refused: words split on spaces, commas, semicolons and <c>#</c>, the <c>#</c> optional, only
/// letters, digits and <c>_</c> kept, repeats dropped (any case, the first spelling
/// wins), at most <see cref="MaxCount"/>. Words are kept without the <c>#</c>.
/// </summary>
public static partial class Hashtags
{
    [GeneratedRegex(@"[\s,;#]+")]
    private static partial Regex Separators();

    /// <summary>YouTube ignores every hashtag on a video that has over 60; 15 keeps well clear.</summary>
    public const int MaxCount = 15;

    private const string ShortsTag = "Shorts";

    public static IReadOnlyList<string> Parse(string? text)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Any space, comma, semicolon or # ends a tag: "#Battlefield#clutch" is two.
        foreach (var word in Separators().Split(text ?? ""))
        {
            var tag = new string(word.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            tags.Add(tag);
            if (tags.Count == MaxCount) break;
        }
        return tags;
    }

    /// <summary>The tags as typed on YouTube: <c>#a #b</c>.</summary>
    public static string Format(IEnumerable<string> tags) => string.Join(' ', tags.Select(t => "#" + t));

    /// <summary>A Short's tags: the uploader's first, then <c>#Shorts</c> once, last.</summary>
    public static IReadOnlyList<string> WithShorts(IReadOnlyList<string> tags) =>
        [.. tags.Where(t => !t.Equals(ShortsTag, StringComparison.OrdinalIgnoreCase)), ShortsTag];

    /// <summary>
    /// What the form pre-fills for a clip from <paramref name="channelId"/>
    /// (<see cref="Models.BotConfig.VideoHashtagsByChannel"/>); null when nothing is set.
    /// </summary>
    public static string? DefaultFor(IReadOnlyDictionary<string, string> byChannel, ulong? channelId) =>
        channelId is { } id && byChannel.TryGetValue(id.ToString(), out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
