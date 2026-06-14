using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// The single source of UFC data for the bot, backed by the API-Sports MMA feed
/// (<see href="https://api-sports.io/documentation/mma/v1"/>). Powers all three
/// surfaces — <c>/ufc-schedule</c> (upcoming), <c>/ufc-results</c> (latest card),
/// and the day-before <see cref="UfcReminderService"/> reminder.
///
/// ── Why date scanning ──
/// The free API-Sports plan locks the <c>season</c>, <c>next</c>, and <c>last</c>
/// parameters (season is limited to 2022-2024; next/last are paid-only). The only
/// free discovery parameter is <c>date</c>. So we scan a bounded window of dates
/// around today, one <c>?date=YYYY-MM-DD</c> call each, and group the returned
/// fights into events. To stay well under the 100-calls/day free cap, the whole
/// window is fetched once and cached for <see cref="EventsTtl"/>; every surface
/// reads that one cache. ~{back+forward+1} calls per refresh × a handful of
/// refreshes/day.
///
/// ── Shape ──
/// API-Sports has no "event" object; it returns individual fights, each tagged
/// with the card <c>slug</c> (the event name), a UTC <c>date</c>, <c>is_main</c>,
/// weight class, and per-fighter winner flags. We group fights by slug into
/// <see cref="UfcEvent"/>s.
///
/// ── Auth ──
/// Direct API-Sports accounts use the <c>x-apisports-key</c> header against
/// <c>v1.mma.api-sports.io</c>. Key empty ⇒ <see cref="IsConfigured"/> false and
/// callers say "not set up yet".
/// </summary>
public sealed class UfcApiService
{
    private const string DefaultHost = "v1.mma.api-sports.io";
    private const string KeyHeader = "x-apisports-key";

    // Window scanned around today. UFC runs ~weekly, so ±~9 days reliably catches
    // the most recent completed card and the next one or two upcoming.
    private const int ScanDaysBack = 8;
    private const int ScanDaysForward = 9;

    // Cache the full window so hourly reminder polls + on-demand commands share
    // one fetch. 6h keeps us comfortably under 100 calls/day (≈18 calls × 4/day).
    private static readonly TimeSpan EventsTtl = TimeSpan.FromHours(6);
    // After a fetch that came back empty — transient outage, or the daily quota is
    // exhausted — retry on this cadence instead of serving "nothing" for a full
    // TTL. Long enough not to hammer the API while quota-limited (resets 00:00 UTC),
    // and we never clobber good cached data on an empty result.
    private static readonly TimeSpan EmptyRetryTtl = TimeSpan.FromMinutes(30);

    // Pace the per-day scan to respect the free plan's per-minute rate limit
    // (10 req/min). ~7s between calls ⇒ ≈8.5/min, safely under. A scan therefore
    // takes ~2 min, but it runs in the background and is cached for 6h, so users
    // hit a warm cache and never wait on it.
    private static readonly TimeSpan ThrottleDelay = TimeSpan.FromSeconds(7);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcApiService> _logger;
    private readonly BotConfig _config;

    private readonly object _cacheLock = new();
    private List<UfcEvent> _events = new();
    private DateTime _eventsExpireUtc = DateTime.MinValue;
    private Task? _refreshTask;
    private volatile bool _loaded;

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

    private string Host => string.IsNullOrWhiteSpace(_config.UfcApiHost)
        ? DefaultHost
        : _config.UfcApiHost.Trim();

    /// <summary>
    /// True once at least one scan has completed. Lets callers distinguish "still
    /// warming up the cache" from "scanned and genuinely nothing found".
    /// </summary>
    public bool IsWarmedUp => _loaded;

    /// <summary>Upcoming events (soonest first), grouped from the scanned window.</summary>
    public Task<IReadOnlyList<UfcEvent>> GetUpcomingAsync(int max = 8, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        IReadOnlyList<UfcEvent> result = Snapshot()
            .Where(e => e.StartUtc is { } s && s >= nowUtc.AddHours(-3)) // grace for an in-progress card
            .OrderBy(e => e.StartUtc)
            .Take(max)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>The most recently completed event with results, or null if none found.</summary>
    public Task<UfcEvent?> GetLatestEventResultsAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var result = Snapshot()
            .Where(e => e.StartUtc is { } s && s <= nowUtc.AddHours(6) // grace for an in-progress card
                        && e.Fights.Any(f => f.Fighters.Any(x => x.Winner == true)))
            .OrderByDescending(e => e.StartUtc)
            .FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns the current cached events instantly (stale-while-revalidate) and
    /// kicks off a background refresh if the cache is stale. Reads NEVER block on
    /// the network — critical because slash-command handlers run on Discord's
    /// gateway thread, and a slow inline scan there starves the heartbeat and
    /// drops the connection. The throttled ~2-minute scan only ever runs on a
    /// background task.
    /// </summary>
    private List<UfcEvent> Snapshot()
    {
        TriggerRefreshIfStale();
        lock (_cacheLock)
        {
            return _events;
        }
    }

    private void TriggerRefreshIfStale()
    {
        if (!IsConfigured) return;

        lock (_cacheLock)
        {
            if (DateTime.UtcNow < _eventsExpireUtc) return;       // still fresh
            if (_refreshTask is { IsCompleted: false }) return;   // already refreshing
            _refreshTask = Task.Run(RefreshAsync);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var fights = await ScanWindowAsync(CancellationToken.None);
            var events = GroupIntoEvents(fights);

            lock (_cacheLock)
            {
                if (events.Count > 0)
                {
                    _events = events;
                    _eventsExpireUtc = DateTime.UtcNow.Add(EventsTtl);
                }
                else
                {
                    // Keep whatever we had; retry sooner than a full TTL.
                    _eventsExpireUtc = DateTime.UtcNow.Add(EmptyRetryTtl);
                }
                _loaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UfcApiService background refresh failed");
            lock (_cacheLock)
            {
                _eventsExpireUtc = DateTime.UtcNow.Add(EmptyRetryTtl);
            }
        }
    }

    private async Task<List<ApiSportsFight>> ScanWindowAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var all = new List<ApiSportsFight>();

        var first = true;
        for (var offset = -ScanDaysBack; offset <= ScanDaysForward; offset++)
        {
            // Throttle between calls (not before the first) to stay under the
            // per-minute rate limit.
            if (!first)
                await Task.Delay(ThrottleDelay, ct);
            first = false;

            var day = today.AddDays(offset);
            var url = $"https://{Host}/fights?date={day:yyyy-MM-dd}";
            var resp = await GetJsonAsync<ApiSportsResponse<ApiSportsFight>>(url, ct);
            if (resp?.Response is { Count: > 0 } fights)
                all.AddRange(fights);
        }

        _logger.LogInformation(
            "UfcApiService scanned {Days} days, found {Fights} fights",
            ScanDaysBack + ScanDaysForward + 1, all.Count);

        return all;
    }

    /// <summary>
    /// Groups raw fights into events by card slug. Winner is listed first in each
    /// bout; the main event (is_main) gets a high Order so the embed sorts it on top.
    /// </summary>
    private static List<UfcEvent> GroupIntoEvents(IReadOnlyList<ApiSportsFight> fights)
    {
        return fights
            .Where(f => f.Fighters?.First is not null && f.Fighters?.Second is not null)
            .Select(f => (fight: f, start: ParseUtc(f)))
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.fight.Slug)
                ? x.fight.Slug!.Trim()
                : (x.start?.ToString("yyyy-MM-dd") ?? "Unknown"))
            .Select(g =>
            {
                var ev = new UfcEvent
                {
                    Key = g.Key,
                    Name = g.Key,
                    StartUtc = g.Min(x => x.start),
                    Status = g.Any(x => x.fight.IsFinished) ? "Final" : "Scheduled",
                };

                foreach (var (fight, _) in g)
                {
                    var first = fight.Fighters!.First!;
                    var second = fight.Fighters!.Second!;
                    ev.Fights.Add(new UfcFight
                    {
                        FightId = fight.Id,
                        WeightClass = fight.Category,
                        Order = fight.IsMain == true ? 1000 : 0,
                        Fighters = new List<UfcFighter>
                        {
                            new() { FighterId = first.Id,  FirstName = first.Name,  Winner = first.Winner,  Logo = first.Logo },
                            new() { FighterId = second.Id, FirstName = second.Name, Winner = second.Winner, Logo = second.Logo },
                        },
                    });
                }

                return ev;
            })
            .ToList();
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Trim defensively — a stray space/newline from a .env file would
            // otherwise be sent as part of the key and rejected by the API.
            req.Headers.Add(KeyHeader, _config.UfcApiKey.Trim());
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("API-Sports MMA returned {Status} for {Url}", (int)resp.StatusCode, Redact(url));
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);

            // API-Sports always returns 200; failures surface in the "errors" field
            // (e.g. plan restrictions). Surface them so config issues aren't silent.
            LogApiErrors(json, url);

            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API-Sports MMA fetch failed for {Url}", Redact(url));
            return null;
        }
    }

    private void LogApiErrors(string json, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("errors", out var errors)) return;

            // "errors" is [] when fine, or an object of {field: message} when not.
            if (errors.ValueKind == JsonValueKind.Object && errors.EnumerateObject().Any())
            {
                var msg = string.Join("; ", errors.EnumerateObject()
                    .Select(e => $"{e.Name}: {e.Value}"));
                _logger.LogWarning("API-Sports MMA returned errors for {Url}: {Errors}", Redact(url), msg);
            }
        }
        catch
        {
            // Best-effort diagnostics only.
        }
    }

    private static string Redact(string url)
    {
        var q = url.IndexOf('?');
        return q >= 0 ? url[..q] : url;
    }

    private static DateTime? ParseUtc(ApiSportsFight f)
    {
        if (f.Timestamp is { } ts && ts > 0)
            return DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;

        if (!string.IsNullOrWhiteSpace(f.Date) &&
            DateTimeOffset.TryParse(f.Date, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime;

        return null;
    }
}
