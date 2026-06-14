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
/// Why API-Sports and not SportsDataIO: SportsDataIO's free tier scrambles result
/// fields and (as we saw live) ships synthetic event names like "UFC Freedom 250",
/// which break poster lookups. API-Sports returns real, unscrambled cards on its
/// free tier (100 calls/day) and carries the true event name in each fight's
/// <c>slug</c> — real matchups and working Wikipedia posters from one provider.
///
/// ── Shape ──
/// API-Sports has no "event" object; it returns individual fights, each tagged
/// with the card <c>slug</c>, a UTC <c>date</c>, <c>is_main</c>, weight class, and
/// per-fighter winner flags. We pull a season of fights and group them by slug
/// into <see cref="UfcEvent"/>s.
///
/// ── Calls / caching ──
/// One request per season, cached ~10 min and shared by all three surfaces, so a
/// weekly results post plus hourly reminder polls stay far under the daily cap.
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

    private static readonly TimeSpan SeasonTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcApiService> _logger;
    private readonly BotConfig _config;

    private readonly object _cacheLock = new();
    private readonly Dictionary<int, (List<ApiSportsFight> Fights, DateTime AtUtc)> _seasonCache = new();

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

    /// <summary>Upcoming events (soonest first), grouped from future-dated fights.</summary>
    public async Task<IReadOnlyList<UfcEvent>> GetUpcomingAsync(int max = 8, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var fights = await GetFightsAroundAsync(nowUtc, ct);

        var events = GroupIntoEvents(fights)
            .Where(e => e.StartUtc is { } s && s >= nowUtc.AddHours(-3)) // grace for an in-progress card
            .OrderBy(e => e.StartUtc)
            .Take(max)
            .ToList();

        return events;
    }

    /// <summary>The most recently completed event with its bouts, or null if none found.</summary>
    public async Task<UfcEvent?> GetLatestEventResultsAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var fights = await GetFightsAroundAsync(nowUtc, ct);

        return GroupIntoEvents(fights)
            .Where(e => e.StartUtc is { } s && s <= nowUtc.AddHours(6) // grace for an in-progress card
                        && e.Fights.Any(f => f.Fighters.Any(x => x.Winner == true)))
            .OrderByDescending(e => e.StartUtc)
            .FirstOrDefault();
    }

    private async Task<List<ApiSportsFight>> GetFightsAroundAsync(DateTime nowUtc, CancellationToken ct)
    {
        var fights = new List<ApiSportsFight>(await GetSeasonFightsAsync(nowUtc.Year, ct));
        if (nowUtc.Month == 12)
            fights.AddRange(await GetSeasonFightsAsync(nowUtc.Year + 1, ct));
        else if (nowUtc.Month == 1)
            fights.AddRange(await GetSeasonFightsAsync(nowUtc.Year - 1, ct));
        return fights;
    }

    private async Task<IReadOnlyList<ApiSportsFight>> GetSeasonFightsAsync(int season, CancellationToken ct)
    {
        if (!IsConfigured) return Array.Empty<ApiSportsFight>();

        lock (_cacheLock)
        {
            if (_seasonCache.TryGetValue(season, out var hit) && DateTime.UtcNow - hit.AtUtc < SeasonTtl)
                return hit.Fights;
        }

        var url = $"https://{Host}/fights?season={season}";
        var fights = await GetJsonAsync<ApiSportsResponse<ApiSportsFight>>(url, ct);
        var list = fights?.Response ?? new List<ApiSportsFight>();

        lock (_cacheLock)
        {
            _seasonCache[season] = (list, DateTime.UtcNow);
        }

        return list;
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
                _logger.LogWarning("API-Sports MMA returned {Status} for {Url}", (int)resp.StatusCode, Redact(url));
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
            _logger.LogError(ex, "API-Sports MMA fetch failed for {Url}", Redact(url));
            return default;
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
