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
    // After a fetch that came back empty (transient outage), retry sooner instead
    // of serving "nothing" for a full TTL — and never clobber good cached data.
    private static readonly TimeSpan EmptyRetryTtl = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcApiService> _logger;
    private readonly BotConfig _config;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _cacheLock = new();
    private List<UfcEvent> _events = new();
    private DateTime _eventsExpireUtc = DateTime.MinValue;

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

    /// <summary>Upcoming events (soonest first), grouped from the scanned window.</summary>
    public async Task<IReadOnlyList<UfcEvent>> GetUpcomingAsync(int max = 8, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        return (await GetEventsAsync(ct))
            .Where(e => e.StartUtc is { } s && s >= nowUtc.AddHours(-3)) // grace for an in-progress card
            .OrderBy(e => e.StartUtc)
            .Take(max)
            .ToList();
    }

    /// <summary>The most recently completed event with results, or null if none found.</summary>
    public async Task<UfcEvent?> GetLatestEventResultsAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        return (await GetEventsAsync(ct))
            .Where(e => e.StartUtc is { } s && s <= nowUtc.AddHours(6) // grace for an in-progress card
                        && e.Fights.Any(f => f.Fighters.Any(x => x.Winner == true)))
            .OrderByDescending(e => e.StartUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the cached window of events, refreshing it (one fetch at a time)
    /// when stale. Shared by every surface so we only pay the scan once per TTL.
    /// </summary>
    private async Task<IReadOnlyList<UfcEvent>> GetEventsAsync(CancellationToken ct)
    {
        if (!IsConfigured) return Array.Empty<UfcEvent>();

        lock (_cacheLock)
        {
            if (DateTime.UtcNow < _eventsExpireUtc)
                return _events;
        }

        // Single-flight: if another caller is already refreshing, wait and reuse.
        await _refreshGate.WaitAsync(ct);
        try
        {
            lock (_cacheLock)
            {
                if (DateTime.UtcNow < _eventsExpireUtc)
                    return _events;
            }

            var fights = await ScanWindowAsync(ct);
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
                return _events;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<List<ApiSportsFight>> ScanWindowAsync(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;
        var all = new List<ApiSportsFight>();

        for (var offset = -ScanDaysBack; offset <= ScanDaysForward; offset++)
        {
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
                            new() { FighterId = first.Id,  FirstName = first.Name,  Winner = first.Winner },
                            new() { FighterId = second.Id, FirstName = second.Name, Winner = second.Winner },
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
            req.Headers.Add(KeyHeader, _config.UfcApiKey);
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
