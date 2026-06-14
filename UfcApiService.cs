using System.Collections.Concurrent;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Thin client over the SportsDataIO MMA "Scores" feed. Two operations the bot
/// needs: the season <b>Schedule</b> (a list of events) and a single
/// <b>Event</b> (the full fight card with results once they're in).
///
/// ── Why a shared singleton with a cache ──
/// Both the <c>/ufc-schedule</c> / <c>/ufc-results</c> commands and the
/// day-before <see cref="UfcReminderService"/> read the same data. SportsDataIO
/// free-trial keys are call-metered, so every read goes through a short
/// in-memory cache (schedule ~15 min, event ~5 min). A slash command and a
/// reminder tick landing seconds apart share one upstream call.
///
/// ── Key handling ──
/// The subscription key is sent in the <c>Ocp-Apim-Subscription-Key</c> header
/// (not the query string) so it never lands in a logged URL. If
/// <see cref="BotConfig.UfcApiKey"/> is empty the service reports
/// <see cref="IsConfigured"/> = false and every call returns empty — callers
/// surface a friendly "not set up yet" message instead of erroring.
///
/// ── Time zone ──
/// SportsDataIO timestamps are US Eastern with no offset. <see cref="ToUtc"/>
/// converts them to UTC so Discord's &lt;t:unix&gt; markdown localizes per
/// viewer. Events with no time-of-day (only a Day) fall back to the Day value.
/// </summary>
public sealed class UfcApiService
{
    private const string BaseUrl = "https://api.sportsdata.io/v3/mma/scores/json";
    private const string KeyHeader = "Ocp-Apim-Subscription-Key";

    private static readonly TimeSpan ScheduleTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan EventTtl    = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcApiService> _logger;
    private readonly BotConfig _config;

    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<UfcEvent>>> _scheduleCache = new();
    private readonly ConcurrentDictionary<int, CacheEntry<UfcEvent?>> _eventCache = new();

    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    public UfcApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<UfcApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.UfcApiKey);

    /// <summary>The configured league code (default "UFC").</summary>
    private string League => string.IsNullOrWhiteSpace(_config.UfcLeague) ? "UFC" : _config.UfcLeague.Trim();

    /// <summary>
    /// Returns the full schedule for a season (the year, e.g. 2026), newest
    /// caching honored. Empty list on any failure or when unconfigured.
    /// </summary>
    public async Task<IReadOnlyList<UfcEvent>> GetSeasonScheduleAsync(int season, CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<UfcEvent>();

        var cacheKey = $"{League}:{season}";
        if (_scheduleCache.TryGetValue(cacheKey, out var hit) && !hit.IsStale)
            return hit.Value;

        var url = $"{BaseUrl}/Schedule/{League}/{season}";
        var events = await GetJsonAsync<List<UfcEvent>>(url, ct) ?? new List<UfcEvent>();
        IReadOnlyList<UfcEvent> result = events;
        _scheduleCache[cacheKey] = new CacheEntry<IReadOnlyList<UfcEvent>>(result, ScheduleTtl);
        return result;
    }

    /// <summary>
    /// Returns a single event with its full fight card and results, or null on
    /// failure / when unconfigured.
    /// </summary>
    public async Task<UfcEvent?> GetEventAsync(int eventId, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        if (_eventCache.TryGetValue(eventId, out var hit) && !hit.IsStale)
            return hit.Value;

        var url = $"{BaseUrl}/Event/{eventId}";
        var ev = await GetJsonAsync<UfcEvent>(url, ct);
        _eventCache[eventId] = new CacheEntry<UfcEvent?>(ev, EventTtl);
        return ev;
    }

    /// <summary>
    /// Convenience: events across the current season (and the next, near the
    /// year boundary) whose start is in the future, soonest first.
    /// </summary>
    public async Task<IReadOnlyList<UfcEvent>> GetUpcomingAsync(int max = 5, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var events = await GetSeasonsAroundAsync(nowUtc, ct);

        return events
            .Select(e => (ev: e, start: ToUtc(e)))
            .Where(x => x.start is { } s && s >= nowUtc.AddHours(-3)) // small grace so an in-progress card still shows
            .OrderBy(x => x.start)
            .Take(max)
            .Select(x => x.ev)
            .ToList();
    }

    /// <summary>
    /// Convenience: the most recently started event (the one whose results you'd
    /// want from <c>/ufc-results</c>), fetched in full detail.
    /// </summary>
    public async Task<UfcEvent?> GetMostRecentEventAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var events = await GetSeasonsAroundAsync(nowUtc, ct);

        var recent = events
            .Select(e => (ev: e, start: ToUtc(e)))
            .Where(x => x.start is { } s && s <= nowUtc)
            .OrderByDescending(x => x.start)
            .Select(x => x.ev)
            .FirstOrDefault();

        if (recent is null) return null;

        // The schedule feed carries fights but not always full result detail;
        // the Event endpoint is authoritative for outcomes.
        return await GetEventAsync(recent.EventId, ct) ?? recent;
    }

    private async Task<List<UfcEvent>> GetSeasonsAroundAsync(DateTime nowUtc, CancellationToken ct)
    {
        var events = new List<UfcEvent>(await GetSeasonScheduleAsync(nowUtc.Year, ct));

        // Near the turn of the year, pull the neighbor season so "upcoming" and
        // "most recent" don't fall off a cliff on Dec 31 / Jan 1.
        if (nowUtc.Month == 12)
            events.AddRange(await GetSeasonScheduleAsync(nowUtc.Year + 1, ct));
        else if (nowUtc.Month == 1)
            events.AddRange(await GetSeasonScheduleAsync(nowUtc.Year - 1, ct));

        return events;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add(KeyHeader, _config.UfcApiKey);
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "SportsDataIO returned {Status} for {Url}",
                    (int)resp.StatusCode, Redact(url));
                return default;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SportsDataIO fetch failed for {Url}", Redact(url));
            return default;
        }
    }

    private static string Redact(string url)
    {
        var q = url.IndexOf('?');
        return q >= 0 ? url[..q] : url;
    }

    /// <summary>
    /// Parses an event's Eastern start time into UTC. Prefers the time-of-day
    /// <see cref="UfcEvent.DateTime"/>; falls back to <see cref="UfcEvent.Day"/>.
    /// Returns null if neither parses.
    /// </summary>
    public static DateTime? ToUtc(UfcEvent ev)
    {
        var raw = !string.IsNullOrWhiteSpace(ev.DateTime) ? ev.DateTime : ev.Day;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var local))
            return null;

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, Eastern);
    }

    private static TimeZoneInfo ResolveEastern()
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById("America/New_York", out var tz) && tz is not null)
            return tz;
        // Windows fallback id, just in case this ever runs off Linux.
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Eastern Standard Time", out var win) && win is not null)
            return win;
        return TimeZoneInfo.Utc;
    }

    private readonly struct CacheEntry<T>
    {
        public T Value { get; }
        private readonly DateTime _expiresUtc;
        public CacheEntry(T value, TimeSpan ttl)
        {
            Value = value;
            _expiresUtc = DateTime.UtcNow.Add(ttl);
        }
        public bool IsStale => DateTime.UtcNow >= _expiresUtc;
    }
}
