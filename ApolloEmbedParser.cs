using System.Text.RegularExpressions;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Shared parser for Apollo bot event embeds.
/// Handles two time formats Apollo uses:
///
///   1. Discord timestamps — <t:UNIX:F> in the embed body/fields (older Apollo versions)
///   2. Plain text in a "Time" field — "Saturday, March 28, 2026 at 18:00 – 20:00"
///      (current Apollo format as of 2026)
///
/// Both ApolloEventHandler and ApolloBackfillService use this class so any
/// format fix only needs to be made in one place.
/// </summary>
public static partial class ApolloEmbedParser
{
    // ─── Regex patterns ──────────────────────────────────────────────

    // <t:1234567890> or <t:1234567890:F> etc.
    [GeneratedRegex(@"<t:(\d+)(?::[a-zA-Z])?>", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    // <@123456789> or <@!123456789>
    [GeneratedRegex(@"<@!?(\d+)>", RegexOptions.Compiled)]
    private static partial Regex MentionRegex();

    // Matches Apollo's plain-text time field:
    //   "Saturday, March 28, 2026 at 18:00 – 20:00 [Add to Google]"
    //   "Saturday, March 28, 2026 at 18:00"
    // The end time and "[Add to Google]" parts are optional.
    // Uses both en-dash (–) and hyphen (-) as range separators.
    [GeneratedRegex(
        @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\s+" +
        @"(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+" +
        @"\d{1,2},\s+\d{4}\s+at\s+(\d{1,2}:\d{2})" +
        @"(?:\s*[–\-]\s*(\d{1,2}:\d{2}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PlainTextTimeRegex();

    // Matches the date portion independently for parsing: "March 28, 2026"
    [GeneratedRegex(
        @"((?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},\s+\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex DatePortionRegex();

    // ─── Public API ──────────────────────────────────────────────────

    public sealed record ParsedApolloEvent
    {
        public string   Title         { get; init; } = "";
        public DateTime StartUtc      { get; init; }
        public DateTime EndUtc        { get; init; }
        public ulong?   OrganizerId   { get; init; }
        public string?  OrganizerName { get; init; }
        public string?  Description   { get; init; }
    }

    /// <summary>
    /// Parses an Apollo embed into a structured event record.
    /// Returns null if a title or start time cannot be extracted.
    /// </summary>
    public static ParsedApolloEvent? Parse(IEmbed embed)
    {
        // ── Title ─────────────────────────────────────────────────────
        var title = !string.IsNullOrWhiteSpace(embed.Title)
            ? embed.Title
            : embed.Author?.Name;

        if (string.IsNullOrWhiteSpace(title)) return null;

        // ── Time parsing ──────────────────────────────────────────────
        var allText = BuildEmbedText(embed);

        DateTime startUtc;
        DateTime endUtc;

        // Strategy 1: Discord unix timestamps (<t:UNIX:X>) anywhere in the embed
        var tsMatches = TimestampRegex().Matches(allText);
        if (tsMatches.Count > 0)
        {
            var timestamps = tsMatches
                .Select(m => DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).UtcDateTime)
                .OrderBy(t => t)
                .ToList();

            startUtc = timestamps[0];
            endUtc   = timestamps.Count >= 2 && timestamps[1] > startUtc.AddMinutes(15)
                ? timestamps[1]
                : startUtc.AddMinutes(ParseDurationMinutes(embed));
        }
        // Strategy 2: Apollo plain-text "Time" field
        // e.g. "Saturday, March 28, 2026 at 18:00 – 20:00 [Add to Google]"
        else
        {
            var timeFieldValue = FindTimeFieldValue(embed);
            if (timeFieldValue is null) return null;

            var parsed = ParsePlainTextTime(timeFieldValue);
            if (parsed is null) return null;

            (startUtc, endUtc) = parsed.Value;

            // If no end time was present, fall back to duration field or 2-hour default
            if (endUtc == startUtc)
                endUtc = startUtc.AddMinutes(ParseDurationMinutes(embed));
        }

        // ── Organizer ─────────────────────────────────────────────────
        ulong? organizerId = null;

        var organizerFieldValue = embed.Fields
            .FirstOrDefault(f =>
                f.Name.Contains("organizer",  StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("host",       StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("created by", StringComparison.OrdinalIgnoreCase))
            .Value;

        // Also check the description and footer for "Created by ..."
        var createdByText = embed.Fields
            .FirstOrDefault(f => f.Value.Contains("Created by", StringComparison.OrdinalIgnoreCase))
            .Value ?? "";

        if (string.IsNullOrEmpty(createdByText) &&
            (embed.Description?.Contains("Created by", StringComparison.OrdinalIgnoreCase) == true))
            createdByText = embed.Description;

        if (string.IsNullOrEmpty(createdByText) &&
            (embed.Footer?.Text.Contains("Created by", StringComparison.OrdinalIgnoreCase) == true))
            createdByText = embed.Footer.Value.Text;

        var mentionSource = !string.IsNullOrEmpty(organizerFieldValue)
            ? organizerFieldValue
            : !string.IsNullOrEmpty(createdByText)
                ? createdByText
                : embed.Description ?? "";

        var mentionMatch = MentionRegex().Match(mentionSource);
        if (mentionMatch.Success && ulong.TryParse(mentionMatch.Groups[1].Value, out var uid))
            organizerId = uid;

        return new ParsedApolloEvent
        {
            Title         = title.Trim(),
            StartUtc      = startUtc,
            EndUtc        = endUtc,
            OrganizerId   = organizerId,
            OrganizerName = embed.Author?.Name,
            Description   = embed.Description
        };
    }

    // ─── Plain-text time parsing ─────────────────────────────────────

    /// <summary>
    /// Finds the value of whichever embed field contains the event time.
    /// Apollo typically uses a field named "Time", "Date", or "When".
    /// Falls back to scanning the full embed text.
    /// </summary>
    private static string? FindTimeFieldValue(IEmbed embed)
    {
        // Prefer a field explicitly named "Time", "Date", or "When"
        var namedField = embed.Fields.FirstOrDefault(f =>
            f.Name.Equals("time", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Equals("date", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Equals("when", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(namedField.Value))
            return namedField.Value;

        // Fall back to any field whose value contains the plain-text pattern
        var anyField = embed.Fields.FirstOrDefault(f =>
            PlainTextTimeRegex().IsMatch(f.Value));

        if (!string.IsNullOrEmpty(anyField.Value))
            return anyField.Value;

        // Last resort: description
        if (!string.IsNullOrEmpty(embed.Description) &&
            PlainTextTimeRegex().IsMatch(embed.Description))
            return embed.Description;

        return null;
    }

    /// <summary>
    /// Parses a string like "Saturday, March 28, 2026 at 18:00 – 20:00 [Add to Google]"
    /// into (startUtc, endUtc). Times are treated as UTC since Apollo posts in UTC.
    /// Returns null if parsing fails.
    /// </summary>
    private static (DateTime start, DateTime end)? ParsePlainTextTime(string text)
    {
        var timeMatch = PlainTextTimeRegex().Match(text);
        if (!timeMatch.Success) return null;

        var dateMatch = DatePortionRegex().Match(text);
        if (!dateMatch.Success) return null;

        var dateStr  = dateMatch.Groups[1].Value;          // "March 28, 2026"
        var startStr = timeMatch.Groups[1].Value;          // "18:00"
        var endStr   = timeMatch.Groups[2].Value;          // "20:00" (may be empty)

        if (!DateOnly.TryParse(dateStr, out var date)) return null;
        if (!TimeOnly.TryParse(startStr, out var startTime)) return null;

        var start = DateTime.SpecifyKind(date.ToDateTime(startTime), DateTimeKind.Utc);

        DateTime end;
        if (!string.IsNullOrEmpty(endStr) && TimeOnly.TryParse(endStr, out var endTime))
        {
            end = DateTime.SpecifyKind(date.ToDateTime(endTime), DateTimeKind.Utc);
            // Handle midnight crossover
            if (end <= start) end = end.AddDays(1);
        }
        else
        {
            end = start; // caller will apply duration fallback
        }

        return (start, end);
    }

    // ─── Shared helpers ──────────────────────────────────────────────

    public static string BuildEmbedText(IEmbed embed)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(embed.Description)) parts.Add(embed.Description);
        foreach (var field in embed.Fields)
        {
            parts.Add(field.Name);
            parts.Add(field.Value);
        }
        if (!string.IsNullOrEmpty(embed.Footer?.Text)) parts.Add(embed.Footer.Value.Text);
        return string.Join("\n", parts);
    }

    public static int ParseDurationMinutes(IEmbed embed)
    {
        var durationText = embed.Fields
            .FirstOrDefault(f =>
                f.Name.Contains("duration", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("length",   StringComparison.OrdinalIgnoreCase))
            .Value;

        if (string.IsNullOrEmpty(durationText)) return 120;

        var hoursMatch = Regex.Match(durationText, @"(\d+)\s*h(?:ours?)?", RegexOptions.IgnoreCase);
        var minsMatch  = Regex.Match(durationText, @"(\d+)\s*m(?:in(?:utes?)?)?", RegexOptions.IgnoreCase);

        var hours   = hoursMatch.Success ? int.Parse(hoursMatch.Groups[1].Value) : 0;
        var minutes = minsMatch.Success  ? int.Parse(minsMatch.Groups[1].Value)  : 0;

        var total = hours * 60 + minutes;
        return total > 0 ? total : 120;
    }
}
