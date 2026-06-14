using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Powers <c>/ufc-results</c> off the API-Sports MMA feed (free tier, real
/// unscrambled outcomes) instead of SportsDataIO, whose free tier scrambles
/// result fields. Fetches the season's fights, groups them into events by
/// <c>slug</c> (the card name), and returns the most recently completed card
/// mapped into the shared <see cref="UfcEvent"/> model so it renders through the
/// same <see cref="UfcEmbedBuilder"/> as everything else.
///
/// ── Calls / caching ──
/// One request per season fetch, cached ~10 min. A single weekly results lookup
/// nowhere near the free tier's 100/day cap, and the cache means a burst of
/// <c>/ufc-results</c> uses shares one upstream call.
///
/// ── Auth ──
/// Direct API-Sports accounts authenticate with the <c>x-apisports-key</c>
/// header against <c>v1.mma.api-sports.io</c>. Key empty ⇒
/// <see cref="IsConfigured"/> false and the command says "not set up yet".
/// </summary>
public sealed class UfcResultsService
{
    private const string DefaultHost = "v1.mma.api-sports.io";
    private const string KeyHeader = "x-apisports-key";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcResultsService> _logger;
    private readonly BotConfig _config;

    private readonly object _cacheLock = new();
    private UfcEvent? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public UfcResultsService(
        IHttpClientFactory httpClientFactory,
        ILogger<UfcResultsService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.UfcResultsApiKey);

    private string Host => string.IsNullOrWhiteSpace(_config.UfcResultsApiHost)
        ? DefaultHost
        : _config.UfcResultsApiHost.Trim();

    /// <summary>
    /// The most recently completed event with its bouts, or null when
    /// unconfigured / nothing found. Mapped to <see cref="UfcEvent"/> for the
    /// shared results embed.
    /// </summary>
    public async Task<UfcEvent?> GetLatestEventResultsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;

        lock (_cacheLock)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAtUtc < CacheTtl)
                return _cached;
        }

        var nowUtc = DateTime.UtcNow;
        var fights = new List<ApiSportsFight>();
        fights.AddRange(await GetSeasonFightsAsync(nowUtc.Year, ct));
        // Cover the year boundary so early-January doesn't miss late-December cards.
        if (nowUtc.Month == 1)
            fights.AddRange(await GetSeasonFightsAsync(nowUtc.Year - 1, ct));

        var ev = BuildLatestEvent(fights, nowUtc);

        lock (_cacheLock)
        {
            _cached = ev;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return ev;
    }

    private async Task<IReadOnlyList<ApiSportsFight>> GetSeasonFightsAsync(int season, CancellationToken ct)
    {
        var url = $"https://{Host}/fights?season={season}";
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add(KeyHeader, _config.UfcResultsApiKey);
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("API-Sports MMA returned {Status} for season {Season}", (int)resp.StatusCode, season);
                return Array.Empty<ApiSportsFight>();
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ApiSportsResponse<ApiSportsFight>>(json, JsonOpts);
            return parsed?.Response ?? new List<ApiSportsFight>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API-Sports MMA fetch failed for season {Season}", season);
            return Array.Empty<ApiSportsFight>();
        }
    }

    /// <summary>
    /// Groups fights into events by card name, picks the most recent completed
    /// card, and maps it into a <see cref="UfcEvent"/> (winner listed first in
    /// each bout, main event flagged so the embed sorts it to the top).
    /// </summary>
    private static UfcEvent? BuildLatestEvent(IReadOnlyList<ApiSportsFight> fights, DateTime nowUtc)
    {
        var candidates = fights
            .Where(f => f.IsFinished && f.Fighters?.First is not null && f.Fighters?.Second is not null)
            .Select(f => (fight: f, start: ParseUtc(f)))
            .Where(x => x.start is { } s && s <= nowUtc.AddHours(6)) // small grace for an in-progress card
            .ToList();

        if (candidates.Count == 0) return null;

        // Group into events. Slug is the card name; fall back to the date when absent.
        var groups = candidates
            .GroupBy(x => !string.IsNullOrWhiteSpace(x.fight.Slug)
                ? x.fight.Slug!.Trim()
                : (x.start?.ToString("yyyy-MM-dd") ?? "Unknown"))
            .Select(g => new
            {
                Name = g.Key,
                Start = g.Max(x => x.start) ?? DateTime.MinValue,
                Fights = g.ToList(),
            })
            .OrderByDescending(g => g.Start)
            .ToList();

        var latest = groups.FirstOrDefault();
        if (latest is null) return null;

        var ev = new UfcEvent
        {
            Name = latest.Name,
            StartUtcOverride = latest.Start == DateTime.MinValue ? null : latest.Start,
            Status = "Final",
        };

        foreach (var (fight, _) in latest.Fights)
        {
            var first = fight.Fighters!.First!;
            var second = fight.Fighters!.Second!;

            ev.Fights.Add(new UfcFight
            {
                FightId = fight.Id,
                WeightClass = fight.Category,
                // No numeric card order in this feed; flag the main event so the
                // embed (orders by Order desc) puts it on top.
                Order = fight.IsMain == true ? 1000 : 0,
                Fighters = new List<UfcFighter>
                {
                    new() { FighterId = first.Id,  FirstName = first.Name,  Winner = first.Winner },
                    new() { FighterId = second.Id, FirstName = second.Name, Winner = second.Winner },
                },
            });
        }

        return ev;
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
