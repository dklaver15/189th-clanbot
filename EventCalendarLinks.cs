using ClanGuardBot.Models;
using System.Globalization;
using System.Text;

namespace ClanGuardBot.Services;

/// <summary>
/// Builds "add to calendar" artifacts for an event — a prefilled Google Calendar
/// template URL (opened via a link button) and a downloadable RFC 5545 .ics file
/// (Apple Calendar, Outlook, anything standards-compliant). Pure: no Discord or
/// DB dependencies, so it's trivially testable.
///
/// Times are emitted as UTC instants (<c>yyyyMMddTHHmmssZ</c>). The stored
/// Start/End are already UTC wall-clock values, so the 'Z' suffix is correct
/// regardless of the DateTime's Kind; the user's calendar app renders them in
/// the viewer's local zone, matching the Discord <t:…> stamps on the post.
/// </summary>
public static class EventCalendarLinks
{
    /// <summary>Google Calendar "TEMPLATE" deep link — opens a prefilled event the user can save.</summary>
    public static string GoogleUrl(ClanEvent ev)
    {
        var sb = new StringBuilder("https://calendar.google.com/calendar/render?action=TEMPLATE");
        sb.Append("&text=").Append(Uri.EscapeDataString(ev.Title ?? string.Empty));
        sb.Append("&dates=").Append(Utc(ev.StartUtc)).Append('/').Append(Utc(ev.EndUtc));
        if (!string.IsNullOrWhiteSpace(ev.Description))
            sb.Append("&details=").Append(Uri.EscapeDataString(ev.Description));
        return sb.ToString();
    }

    /// <summary>UTF-8 bytes of the .ics document for attaching to a Discord message.</summary>
    public static byte[] IcsBytes(ClanEvent ev) => Encoding.UTF8.GetBytes(BuildIcs(ev));

    /// <summary>A safe-ish download filename derived from the title, e.g. <c>friday-night-ops.ics</c>.</summary>
    public static string IcsFileName(ClanEvent ev)
    {
        var slug = new string((ev.Title ?? "event").ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        if (slug.Length == 0)  slug = "event";
        if (slug.Length > 40)  slug = slug[..40].Trim('-');
        return $"{slug}.ics";
    }

    /// <summary>The full RFC 5545 VCALENDAR/VEVENT document (CRLF line endings, folded long lines).</summary>
    public static string BuildIcs(ClanEvent ev)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//189th ClanGuard//Events//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:clanguard-{ev.Id}-{ev.MessageId}@189th.clan",
            $"DTSTAMP:{Utc(DateTime.UtcNow)}",
            $"DTSTART:{Utc(ev.StartUtc)}",
            $"DTEND:{Utc(ev.EndUtc)}",
            $"SUMMARY:{Escape(ev.Title)}",
        };
        if (!string.IsNullOrWhiteSpace(ev.Description))
            lines.Add($"DESCRIPTION:{Escape(ev.Description)}");
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(Fold(line)).Append("\r\n");
        return sb.ToString();
    }

    private static string Utc(DateTime dt) => dt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    // RFC 5545 §3.3.11 text escaping: backslash, semicolon and comma are escaped;
    // any newline becomes a literal "\n".
    private static string Escape(string? s) => (s ?? string.Empty)
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n");

    // RFC 5545 §3.1 line folding: no content line should exceed 75 octets. We
    // approximate by character count (fine for the ASCII-dominant content here)
    // and continue overflow on the next line prefixed with a single space.
    private static string Fold(string line)
    {
        if (line.Length <= 75) return line;
        var sb = new StringBuilder(line.Length + 16);
        sb.Append(line, 0, 75);
        var i = 75;
        while (i < line.Length)
        {
            var take = Math.Min(74, line.Length - i);
            sb.Append("\r\n ").Append(line, i, take);
            i += take;
        }
        return sb.ToString();
    }
}
