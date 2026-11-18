using System.Globalization;
using System.Text.RegularExpressions;

namespace ClanGuardBot.Services;

/// <summary>
/// Rewrites Discord timestamp tokens (<c>&lt;t:unix:style&gt;</c>) into fixed
/// Eastern-time text for the places Discord shows as plain text: native poll
/// questions and answers, button labels and embed field names. Those can't show
/// each viewer their own zone, so the real tokens are shown elsewhere instead
/// (see <see cref="LocalTimeLines"/>).
/// </summary>
public static class PollTimestampResolver
{
    public const string ZoneId    = "America/New_York";
    public const string ZoneLabel = "ET";

    private static readonly Regex Token = new(@"<t:(-?\d{1,13})(?::([tTdDfFsSR]))?>", RegexOptions.Compiled);
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(ZoneId);

    public static bool HasTimestamps(string? text) =>
        !string.IsNullOrEmpty(text) && Token.IsMatch(text);

    /// <summary>Replaces every timestamp token in <paramref name="text"/> with Eastern-time text.</summary>
    public static string? Resolve(string? text, DateTime? nowUtc = null)
    {
        if (!HasTimestamps(text)) return text;
        var now = nowUtc ?? DateTime.UtcNow;
        return Token.Replace(text!, m => TryParse(m, out var unix, out var style) ? Format(unix, style, now) : m.Value);
    }

    /// <summary>
    /// One line per distinct timestamp in <paramref name="texts"/>, pairing the
    /// Eastern text with a real token so each viewer sees it in their own zone.
    /// </summary>
    public static IReadOnlyList<string> LocalTimeLines(IEnumerable<string?> texts, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var lines = new List<string>();
        var seen = new HashSet<string>();
        foreach (var text in texts)
        {
            if (!HasTimestamps(text)) continue;
            foreach (Match m in Token.Matches(text!))
            {
                if (!TryParse(m, out var unix, out var style)) continue;
                var fixedText = Format(unix, style, now);
                if (seen.Add(fixedText))
                    lines.Add($"{fixedText} → <t:{unix}:F> (<t:{unix}:R>)");
            }
        }
        return lines;
    }

    /// <summary>
    /// Compact Eastern rendering of one token. Short enough to fit Discord's
    /// 55-character native answer cap. Relative (R) can't stay current in plain
    /// text, so it's shown as an absolute time.
    /// </summary>
    public static string Format(long unix, char style, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unix), Eastern);
        var nowYear = TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)), Eastern).Year;

        var date = local.ToString("ddd, MMM d", Us) + (local.Year != nowYear ? local.ToString(", yyyy", Us) : "");
        var time = local.ToString(style == 'T' ? "h:mm:ss tt" : "h:mm tt", Us) + " " + ZoneLabel;

        return style switch
        {
            't' or 'T' => time,
            'd' or 'D' => date,
            _          => $"{date}, {time}",
        };
    }

    private static bool TryParse(Match m, out long unix, out char style)
    {
        style = m.Groups[2].Success ? m.Groups[2].Value[0] : 'f';
        return long.TryParse(m.Groups[1].Value, out unix)
            && unix >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
            && unix <= DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    }
}
