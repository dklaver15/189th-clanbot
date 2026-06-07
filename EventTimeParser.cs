using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Recognizers.Text;
using System.Globalization;

// The Microsoft.Recognizers.Text.DateTime *namespace* collides with
// System.DateTime — importing it directly makes every `DateTime` ambiguous
// ("'DateTime' is a namespace but is used like a type"). Alias it so `DateTime`
// keeps meaning System.DateTime and the recognizer is reached via `Recog.`.
using Recog = Microsoft.Recognizers.Text.DateTime;

namespace ClanGuardBot.Services;

/// <summary>
/// Turns natural-language time input ("in 2 hours", "7pm tomorrow night",
/// "June 14 at 20:00") into UTC instants, interpreted in the creator's
/// timezone. This is the keystone the event-creation wizard and /timezone both
/// build on.
///
/// ── The three-part time model (kept strictly separate) ──
///   1. Input  — natural language, in the CREATOR's timezone. Parsed here with
///      Microsoft.Recognizers.Text.DateTime against a reference time equal to
///      the creator's current local time, yielding a timezone-naive wall-clock
///      value, then converted to UTC via the creator's <see cref="TimeZoneInfo"/>.
///   2. Storage — always UTC (what this class returns).
///   3. Display — never handled here; the embed emits Discord &lt;t:unix&gt;
///      markdown and each client localizes to the viewer's own zone.
///
/// So the ONLY timezone this class needs is the creator's, and only to read
/// their input. See <see cref="Stamp"/> for the display-side helper.
///
/// ── Ambiguity ──
/// A bare clock value like "7" or "7:30" resolves to TWO candidates (07:30 and
/// 19:30). For event scheduling the soonest FUTURE candidate is almost always
/// what was meant, so that is what we pick, flagging <see
/// cref="TimeParseResult.WasAmbiguous"/> so the caller can surface the resolved
/// time for confirmation.
/// </summary>
public sealed class EventTimeParser
{
    private readonly BotConfig _config;
    private readonly ILogger<EventTimeParser> _logger;

    public EventTimeParser(IOptions<BotConfig> config, ILogger<EventTimeParser> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Common US zones offered as buttons in the creation wizard's timezone
    /// step. Label → IANA id. The wizard also accepts any valid IANA id typed
    /// via an "Other" path (see <see cref="TryResolveTimeZone"/>).
    /// </summary>
    public static readonly IReadOnlyList<(string Label, string IanaId)> CommonZones = new[]
    {
        ("Eastern",  "America/New_York"),
        ("Central",  "America/Chicago"),
        ("Mountain", "America/Denver"),
        ("Arizona",  "America/Phoenix"),
        ("Pacific",  "America/Los_Angeles"),
        ("Alaska",   "America/Anchorage"),
        ("Hawaii",   "Pacific/Honolulu"),
    };

    // Friendly names / abbreviations → IANA, so a member can type "Central" or
    // "CST" instead of "America/Chicago". Any unrecognized input still falls
    // through to a direct IANA lookup.
    private static readonly Dictionary<string, string> ZoneAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eastern"] = "America/New_York",   ["et"] = "America/New_York",
        ["est"] = "America/New_York",       ["edt"] = "America/New_York",
        ["central"] = "America/Chicago",    ["ct"] = "America/Chicago",
        ["cst"] = "America/Chicago",        ["cdt"] = "America/Chicago",
        ["mountain"] = "America/Denver",    ["mt"] = "America/Denver",
        ["mst"] = "America/Denver",         ["mdt"] = "America/Denver",
        ["arizona"] = "America/Phoenix",
        ["pacific"] = "America/Los_Angeles", ["pt"] = "America/Los_Angeles",
        ["pst"] = "America/Los_Angeles",    ["pdt"] = "America/Los_Angeles",
        ["alaska"] = "America/Anchorage",   ["akst"] = "America/Anchorage",
        ["akdt"] = "America/Anchorage",
        ["hawaii"] = "Pacific/Honolulu",    ["hst"] = "Pacific/Honolulu",
    };

    // ──────────────────────────────────────────────────────────────────────
    //  Timezone resolution
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a member-supplied timezone string (an IANA id, or a friendly
    /// alias like "Central"/"CST") to a <see cref="TimeZoneInfo"/>. Returns the
    /// canonical IANA id via <paramref name="ianaId"/> so the caller can
    /// persist it. .NET 8 resolves IANA ids on the Ubuntu droplet via ICU.
    /// </summary>
    public bool TryResolveTimeZone(string input, out TimeZoneInfo tz, out string ianaId)
    {
        tz = TimeZoneInfo.Utc;
        ianaId = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var key = input.Trim();
        if (ZoneAliases.TryGetValue(key, out var mapped))
            key = mapped;

        if (TimeZoneInfo.TryFindSystemTimeZoneById(key, out var found) && found is not null)
        {
            tz = found;
            ianaId = found.Id; // IANA id on Linux
            return true;
        }

        return false;
    }

    /// <summary>
    /// The zone to interpret a creator's input in: their stored
    /// <see cref="UserTimeZone"/> if set and still valid, otherwise
    /// BotConfig.EventDefaultTimeZone. Always returns a usable zone (falls back
    /// to UTC only if even the configured default is invalid, which is logged).
    /// </summary>
    public TimeZoneInfo ResolveZone(string? userIanaId)
    {
        if (!string.IsNullOrWhiteSpace(userIanaId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(userIanaId, out var userTz) && userTz is not null)
            return userTz;

        if (TimeZoneInfo.TryFindSystemTimeZoneById(_config.EventDefaultTimeZone, out var defTz) && defTz is not null)
            return defTz;

        _logger.LogWarning(
            "EventDefaultTimeZone '{Tz}' is not a valid timezone id; falling back to UTC for input parsing",
            _config.EventDefaultTimeZone);
        return TimeZoneInfo.Utc;
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Start-time parsing
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a natural-language start time in the creator's zone. On success
    /// returns the UTC instant; if the phrase was a range ("7-9pm tomorrow")
    /// EndUtc is also populated. If only a date was given (no time of day),
    /// HasTimeOfDay is false and the caller should ask for a time.
    /// </summary>
    public TimeParseResult ParseStart(string input, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(input))
            return TimeParseResult.Fail("I didn't catch a time there — try something like \"7pm tomorrow\" or \"in 2 hours\".");

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var nowUtc   = DateTime.UtcNow;

        List<Recog.ModelResult> results;
        try
        {
            // refTime anchors relative phrases ("tomorrow", "in 2 hours") to the
            // creator's local now, not the server's UTC now.
            results = Recog.DateTimeRecognizer.RecognizeDateTime(
                input, Culture.English, Recog.DateTimeOptions.None,
                DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recognizer threw on input '{Input}'", input);
            return TimeParseResult.Fail("I couldn't understand that time. Try \"7pm tomorrow\" or \"June 14 at 8pm\".");
        }

        // Collect every (start, optional end, hasTime) candidate across results.
        var candidates = new List<(DateTime LocalStart, DateTime? LocalEnd, bool HasTime)>();

        foreach (var result in results)
        {
            foreach (var v in EnumerateValues(result))
            {
                if (!v.TryGetValue("type", out var type))
                    continue;

                switch (type)
                {
                    case "datetime":
                        if (TryParseValue(v, "value", out var dt))
                            candidates.Add((dt, null, true));
                        break;

                    case "date":
                        if (TryParseValue(v, "value", out var d))
                            candidates.Add((d.Date, null, false));
                        break;

                    case "time":
                        // Time without a date → attach to the reference date,
                        // rolling to tomorrow if it's already past.
                        if (TryParseTimeOfDay(v, "value", out var tod))
                        {
                            var local = nowLocal.Date + tod;
                            if (local < nowLocal) local = local.AddDays(1);
                            candidates.Add((local, null, true));
                        }
                        break;

                    case "datetimerange":
                        if (TryParseValue(v, "start", out var rs) && TryParseValue(v, "end", out var re))
                            candidates.Add((rs, re, true));
                        break;

                    case "timerange":
                        if (TryParseTimeOfDay(v, "start", out var ts) && TryParseTimeOfDay(v, "end", out var te))
                        {
                            var ls = nowLocal.Date + ts;
                            if (ls < nowLocal) ls = ls.AddDays(1);
                            var le = ls.Date + te;
                            if (le <= ls) le = le.AddDays(1);
                            candidates.Add((ls, le, true));
                        }
                        break;

                    // "duration", "daterange", "set", etc. are not valid as a
                    // start instant here; ignore.
                }
            }
        }

        if (candidates.Count == 0)
            return TimeParseResult.Fail("I couldn't find a date/time in that. Try \"7pm tomorrow\", \"in 2 hours\", or \"June 14 at 8pm\".");

        // Convert each candidate to UTC, skipping any that don't exist due to a
        // spring-forward DST gap.
        var resolved = new List<(DateTime StartUtc, DateTime? EndUtc, bool HasTime)>();
        foreach (var c in candidates)
        {
            if (!TryLocalToUtc(c.LocalStart, tz, out var startUtc, out _))
                continue;

            DateTime? endUtc = null;
            if (c.LocalEnd is DateTime le && TryLocalToUtc(le, tz, out var eu, out _))
                endUtc = eu;

            resolved.Add((startUtc, endUtc, c.HasTime));
        }

        if (resolved.Count == 0)
            return TimeParseResult.Fail("That time doesn't exist on the chosen date because of a daylight-saving change. Pick a different time.");

        // Prefer the soonest candidate that is still in the future; if none are
        // (e.g. the user explicitly named a past time), take the earliest.
        var future = resolved.Where(r => r.StartUtc >= nowUtc).ToList();
        var pick = (future.Count > 0 ? future : resolved).OrderBy(r => r.StartUtc).First();

        // If a range gave end <= start, treat the end as the next day.
        var pickedEnd = pick.EndUtc;
        if (pickedEnd is DateTime pe && pe <= pick.StartUtc)
            pickedEnd = pe.AddDays(1);

        return new TimeParseResult
        {
            Success      = true,
            StartUtc     = pick.StartUtc,
            EndUtc       = pickedEnd,
            HasTimeOfDay = pick.HasTime,
            WasAmbiguous = resolved.Count > 1,
        };
    }

    // ──────────────────────────────────────────────────────────────────────
    //  End / duration parsing
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the wizard's duration/end step against a known start. Accepts a
    /// duration ("2 hours", "90 minutes") or an explicit end time ("until 9pm",
    /// "9pm"). Enforces end &gt; start and a max length of 24h (matching the
    /// /comp-event guard).
    /// </summary>
    public EndParseResult ParseEnd(string input, DateTime startUtc, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(input))
            return EndParseResult.Fail("Tell me how long it runs (\"2 hours\") or when it ends (\"until 9pm\").");

        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);

        List<Recog.ModelResult> results;
        try
        {
            results = Recog.DateTimeRecognizer.RecognizeDateTime(
                input, Culture.English, Recog.DateTimeOptions.None,
                DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recognizer threw on end/duration input '{Input}'", input);
            return EndParseResult.Fail("I couldn't read that. Try \"2 hours\" or \"until 9pm\".");
        }

        // 1) Prefer an explicit duration.
        foreach (var result in results)
            foreach (var v in EnumerateValues(result))
                if (v.TryGetValue("type", out var t) && t == "duration"
                    && v.TryGetValue("value", out var secStr)
                    && double.TryParse(secStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0)
                {
                    return FinishEnd(startUtc, startUtc.AddSeconds(seconds));
                }

        // 2) Otherwise treat it as an end time-of-day / datetime.
        foreach (var result in results)
            foreach (var v in EnumerateValues(result))
            {
                if (!v.TryGetValue("type", out var type)) continue;

                DateTime endLocal;
                if (type == "datetime" && TryParseValue(v, "value", out var edt))
                    endLocal = edt;
                else if (type == "time" && TryParseTimeOfDay(v, "value", out var tod))
                {
                    endLocal = startLocal.Date + tod;
                    if (endLocal <= startLocal) endLocal = endLocal.AddDays(1); // "9pm" after a 10pm start → next day
                }
                else
                    continue;

                if (!TryLocalToUtc(endLocal, tz, out var endUtc, out var err))
                    return EndParseResult.Fail(err ?? "That end time is invalid; pick another.");

                return FinishEnd(startUtc, endUtc);
            }

        return EndParseResult.Fail("I couldn't read a duration or end time. Try \"2 hours\" or \"until 9pm\".");
    }

    private static EndParseResult FinishEnd(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc)
            return EndParseResult.Fail("The end needs to be after the start.");
        if ((endUtc - startUtc).TotalHours > 24)
            return EndParseResult.Fail("Events can't run longer than 24 hours. Check the start and end.");
        return new EndParseResult { Success = true, EndUtc = endUtc };
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Display helpers (UTC → Discord markdown; localized per viewer by Discord)
    // ──────────────────────────────────────────────────────────────────────

    public static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>
    /// Discord timestamp markdown for a UTC instant. Styles: F=full, f=short,
    /// t=time, D=date, R=relative ("in 3 hours"). Each client renders it in the
    /// viewer's own timezone, which is how per-user localization happens.
    /// </summary>
    public static string Stamp(DateTime utc, char style = 'F') => $"<t:{ToUnix(utc)}:{style}>";

    // ──────────────────────────────────────────────────────────────────────
    //  Recognizer plumbing
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the "values" list out of a ModelResult.Resolution defensively.
    /// Resolution is a SortedDictionary&lt;string,object&gt; whose "values"
    /// entry is a list of string→string dictionaries (each a candidate
    /// resolution carrying "type"/"value" or "start"/"end").
    /// </summary>
    private static IEnumerable<IDictionary<string, string>> EnumerateValues(Recog.ModelResult result)
    {
        if (result?.Resolution is null || !result.Resolution.TryGetValue("values", out var raw) || raw is null)
            yield break;

        if (raw is not System.Collections.IEnumerable list)
            yield break;

        foreach (var item in list)
            if (item is IDictionary<string, string> dict)
                yield return dict;
    }

    private static bool TryParseValue(IDictionary<string, string> v, string key, out DateTime local)
    {
        local = default;
        if (!v.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            return false;

        // Recognizer emits "yyyy-MM-dd HH:mm:ss" (datetime) or "yyyy-MM-dd" (date).
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            local = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }

    private static bool TryParseTimeOfDay(IDictionary<string, string> v, string key, out TimeSpan tod)
    {
        tod = default;
        if (!v.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            return false;

        // Recognizer emits time-of-day as "HH:mm:ss".
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t))
        {
            tod = t;
            return true;
        }
        return false;
    }

    private bool TryLocalToUtc(DateTime local, TimeZoneInfo tz, out DateTime utc, out string? error)
    {
        utc = default;
        error = null;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (tz.IsInvalidTime(local))
        {
            error = "that time doesn't exist on the chosen date because of a daylight-saving change.";
            return false;
        }

        // Ambiguous (fall-back) times resolve to the standard-time offset by
        // default — acceptable for event scheduling.
        utc = TimeZoneInfo.ConvertTimeToUtc(local, tz);
        return true;
    }
}

/// <summary>Result of parsing a natural-language start time.</summary>
public sealed class TimeParseResult
{
    public bool Success { get; init; }

    /// <summary>Human-readable, re-prompt-friendly reason when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    public DateTime StartUtc { get; init; }

    /// <summary>Populated only when the input was a range ("7-9pm tomorrow").</summary>
    public DateTime? EndUtc { get; init; }

    /// <summary>False when only a date was given; the caller should ask for a time.</summary>
    public bool HasTimeOfDay { get; init; }

    /// <summary>True when multiple candidates collapsed (e.g. bare "7" → 7am/7pm); soonest future chosen.</summary>
    public bool WasAmbiguous { get; init; }

    public static TimeParseResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>Result of parsing the duration/end step.</summary>
public sealed class EndParseResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public DateTime EndUtc { get; init; }

    public static EndParseResult Fail(string error) => new() { Success = false, Error = error };
}
