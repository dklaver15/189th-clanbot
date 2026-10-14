using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using ClanGuardBot.Models;

namespace ClanGuardBot.Services;

/// <summary>
/// Reads the upload form (start, end, title) for a video or a Short. Times are
/// <c>m:ss</c>, <c>mm:ss</c> or whole seconds, ASCII digits only; an empty end
/// means the end of the clip. A Short is at most <see cref="MaxShortSeconds"/>
/// long (YouTube's Shorts limit); a video has no limit here.
/// </summary>
public static partial class UploadRequest
{
    public const int MaxShortSeconds = 180;

    // [0-9], not \d: \d also matches non-ASCII digits (e.g. full-width), which
    // int.Parse with the invariant culture then rejects.
    [GeneratedRegex(@"^([0-9]{1,2}):([0-5][0-9])$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^[0-9]{1,4}$")]
    private static partial Regex Plain();

    /// <summary>On failure <paramref name="error"/> is the message for the uploader.</summary>
    public static bool TryParse(
        string start, string? end, string title, VideoSubmissionKind kind, ShortLayout layout,
        [MaybeNullWhen(false)] out UploadSettings settings, out string error)
    {
        settings = null;
        if (!TryParseTime(start, out var from))
        {
            error = "Start must be a time like 0:05 or a number of seconds, like 5.";
            return false;
        }

        double? to = null;
        if (!string.IsNullOrWhiteSpace(end))
        {
            if (!TryParseTime(end, out var parsedEnd))
            {
                error = "End must be a time like 0:30, a number of seconds, or empty for the end of the clip.";
                return false;
            }
            if (parsedEnd <= from)
            {
                error = $"End ({FormatTime(parsedEnd)}) must be after Start ({FormatTime(from)}).";
                return false;
            }
            if (kind == VideoSubmissionKind.Short && parsedEnd - from > MaxShortSeconds)
            {
                error = $"A Short can be at most {FormatTime(MaxShortSeconds)} long, and {FormatTime(from)} to {FormatTime(parsedEnd)} is {FormatTime(parsedEnd - from)}.";
                return false;
            }
            to = parsedEnd;
        }

        var trimmedTitle = title.Trim();
        if (trimmedTitle.Length == 0)
        {
            error = "Title can't be empty.";
            return false;
        }

        settings = new UploadSettings(kind, layout, from, to, trimmedTitle);
        error = string.Empty;
        return true;
    }

    /// <summary>Whole seconds as <c>m:ss</c>, e.g. 65 → "1:05".</summary>
    public static string FormatTime(double seconds) => ClipTime.Format(seconds);

    private static bool TryParseTime(string text, out double seconds)
    {
        text = text.Trim();
        var clock = Clock().Match(text);
        if (clock.Success)
        {
            seconds = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            return true;
        }
        if (Plain().IsMatch(text))
        {
            seconds = int.Parse(text, CultureInfo.InvariantCulture);
            return true;
        }
        seconds = 0;
        return false;
    }
}
