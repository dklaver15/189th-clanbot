using System.Globalization;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// The single source of UFC data for the bot, backed by ESPN's public MMA
/// scoreboard feed (the same endpoint espn.com/the ESPN app use). Powers all
/// three surfaces — <c>/ufc-schedule</c> (upcoming), <c>/ufc-results</c> (latest
/// card), and the day-before <see cref="UfcReminderService"/> reminder.
///
/// ── Why ESPN ──
/// Replaced API-Sports MMA, whose free plan only exposes a ~3-day date window
/// (every out-of-window <c>?date=</c> call returned a plan-restriction error,
/// which silently dried the feature up and burned the 100-calls/day quota). ESPN's
/// scoreboard takes a full date RANGE in one request, requires no API key, and has
/// no documented quota. Caveat: it's undocumented/unofficial, so the response
/// shape could change without notice — the parser is defensive (nullable DTOs,
/// graceful fallbacks) to fail soft if it does.
///
/// ── Shape ──
/// ESPN returns <c>events[]</c>, each already an event with a <c>name</c>, UTC
/// <c>date</c>, a status, and <c>competitions[]</c> — one per bout, in card order
/// (earliest prelim first, main event LAST), each with a weight class
/// (<c>type.abbreviation</c>), round/clock on the bout status, and a per-competitor
/// <c>winner</c> flag. We map straight into <see cref="UfcEvent"/>; the bout index
/// becomes <see cref="UfcFight.Order"/> so the embed's "highest Order = main event"
/// lookup picks the right bout.
///
/// ── No auth ──
/// ESPN needs no key, so <see cref="IsConfigured"/> is always true; the master
/// switch is <see cref="BotConfig.UfcEnabled"/>, checked by the reminder service.
/// </summary>
public sealed class UfcApiService
{
    private const string ScoreboardBase = "https://site.api.espn.com/apis/site/v2/sports/mma/ufc/scoreboard";

    // Window fetched around today. UFC runs ~weekly, so ±~9 days reliably catches
    // the most recent completed card and the next one or two upcoming.
    private const int ScanDaysBack = 8;
    private const int ScanDaysForward = 9;

    // Cache the whole window so hourly reminder polls + on-demand commands share
    // one fetch. ESPN has no quota, but a 6h TTL keeps the feed fresh without
    // hammering and means every surface reads a warm cache.
    private static readonly TimeSpan EventsTtl = TimeSpan.FromHours(6);
    // After a fetch that came back empty (transient outage), retry sooner than a
    // full TTL instead of serving "nothing" for 6h. We never clobber good cached
    // data on an empty result.
    private static readonly TimeSpan EmptyRetryTtl = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcApiService> _logger;

    private readonly object _cacheLock = new();
    private List<UfcEvent> _events = new();
    private DateTime _eventsExpireUtc = DateTime.MinValue;
    private Task? _refreshTask;
    private volatile bool _loaded;

    public UfcApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<UfcApiService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>ESPN needs no API key, so the data source is always available.</summary>
    public bool IsConfigured => true;

    /// <summary>
    /// True once at least one fetch has completed. Lets callers distinguish "still
    /// warming up the cache" from "fetched and genuinely nothing found".
    /// </summary>
    public bool IsWarmedUp => _loaded;

    /// <summary>Upcoming events (soonest first), from the cached window.</summary>
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
    /// gateway thread, and a slow inline fetch there starves the heartbeat and
    /// drops the connection.
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
            var events = await FetchEventsAsync(CancellationToken.None);

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

    /// <summary>
    /// Fetches the whole [today-ScanDaysBack, today+ScanDaysForward] window from
    /// ESPN in a single request and maps it to <see cref="UfcEvent"/>s.
    /// </summary>
    private async Task<List<UfcEvent>> FetchEventsAsync(CancellationToken ct)
    {
        var from = DateTime.UtcNow.Date.AddDays(-ScanDaysBack);
        var to   = DateTime.UtcNow.Date.AddDays(ScanDaysForward);
        var url  = $"{ScoreboardBase}?dates={from:yyyyMMdd}-{to:yyyyMMdd}";

        var board = await GetJsonAsync<EspnScoreboard>(url, ct);
        var events = board?.Events ?? new List<EspnEvent>();

        var mapped = new List<UfcEvent>(events.Count);
        foreach (var e in events)
        {
            var ev = MapEvent(e);
            if (ev is not null) mapped.Add(ev);
        }

        _logger.LogInformation(
            "UfcApiService (ESPN) fetched {Count} event(s) for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}",
            mapped.Count, from, to);

        return mapped;
    }

    private static UfcEvent? MapEvent(EspnEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.Id)) return null;

        var ev = new UfcEvent
        {
            Key      = e.Id!,                       // stable ESPN id → reminder dedupe key
            Name     = e.Name,
            StartUtc = ParseUtc(e.Date),
            Status   = e.Status?.Type?.Description,  // "Final" / "Scheduled"
        };

        var comps = e.Competitions ?? new List<EspnCompetition>();
        for (var i = 0; i < comps.Count; i++)
        {
            var c = comps[i];
            var completed = c.Status?.Type?.Completed == true;

            var fight = new UfcFight
            {
                // ESPN lists earliest prelim first, main event LAST — so the bout
                // index doubles as card order (higher = later = closer to main).
                Order       = i + 1,
                WeightClass = c.Type?.Abbreviation ?? c.Type?.Text,
                Status      = c.Status?.Type?.Description,
                ResultRound = completed ? c.Status?.Period : null,
                ResultClock = completed && c.Status?.Clock is { } clk && clk >= 0
                    ? (int)Math.Round(clk)
                    : null,
                // Finish method (KO/Decision/…) isn't cleanly exposed on the
                // scoreboard feed; the embed shows the matchup + round/clock and
                // omits the method gracefully when it's absent.
            };

            foreach (var p in c.Competitors ?? new List<EspnCompetitor>())
            {
                var name = p.Athlete?.DisplayName ?? p.Athlete?.FullName;
                fight.Fighters.Add(new UfcFighter
                {
                    // Whole display name in FirstName; FullName joins to it. Splitting
                    // is unreliable for multi-word names (e.g. "Édgar Cháirez").
                    FirstName = name,
                    Winner    = p.Winner,
                    Logo      = p.Athlete?.Headshot?.Href,
                });
            }

            ev.Fights.Add(fight);
        }

        return ev;
    }

    private static DateTime? ParseUtc(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcDateTime;
        return null;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ESPN MMA API returned {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
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
            _logger.LogError(ex, "ESPN MMA API fetch failed for {Url}", url);
            return null;
        }
    }

    // ─── ESPN scoreboard DTOs (defensive: everything nullable) ───────────────

    private sealed class EspnScoreboard
    {
        public List<EspnEvent>? Events { get; set; }
    }

    private sealed class EspnEvent
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Date { get; set; }
        public EspnStatus? Status { get; set; }
        public List<EspnCompetition>? Competitions { get; set; }
    }

    private sealed class EspnCompetition
    {
        /// <summary>Weight class lives here (e.g. abbreviation "Welterweight").</summary>
        public EspnWeightType? Type { get; set; }
        public EspnStatus? Status { get; set; }
        public List<EspnCompetitor>? Competitors { get; set; }
    }

    private sealed class EspnWeightType
    {
        public string? Abbreviation { get; set; }
        public string? Text { get; set; }
    }

    private sealed class EspnCompetitor
    {
        public bool? Winner { get; set; }
        public EspnAthlete? Athlete { get; set; }
    }

    private sealed class EspnAthlete
    {
        public string? DisplayName { get; set; }
        public string? FullName { get; set; }
        public EspnImage? Headshot { get; set; }
    }

    private sealed class EspnImage
    {
        public string? Href { get; set; }
    }

    private sealed class EspnStatus
    {
        public double? Clock { get; set; }
        public int? Period { get; set; }
        public EspnStatusType? Type { get; set; }
    }

    private sealed class EspnStatusType
    {
        public string? Name { get; set; }         // STATUS_FINAL / STATUS_SCHEDULED
        public string? Description { get; set; }   // "Final" / "Scheduled"
        public bool? Completed { get; set; }
    }
}
