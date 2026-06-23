using System.Net;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// One leaderboard entry from THE FINALS, flattened from the community API.
/// <see cref="Name"/> is the player's Embark ID (e.g. "Bizzy#0005"); the three
/// platform-name fields are whatever the player has linked (any may be empty).
/// </summary>
public sealed record FinalsEntry(
    int Rank,
    int Change,
    string Name,
    string SteamName,
    string PsnName,
    string XboxName,
    string ClubTag,
    int LeagueNumber,
    string League,
    int RankScore);

/// <summary>
/// A leaderboard entry matched to a roster identifier, with the field that hit.
/// <see cref="MatchedVia"/> is one of "Embark", "Steam", "PSN", "Xbox".
/// </summary>
public sealed record FinalsRankResult(FinalsEntry Entry, string MatchedVia, string MatchedValue);

/// <summary>
/// Client for the community-run THE FINALS leaderboard API
/// (api.the-finals-leaderboard.com — free, no key). Powers <c>/finals-rank</c>,
/// the clan leaderboard board, and rank-up announcements.
///
/// ── What the API exposes ──
/// The ranked leaderboard for the current season is the GLOBAL TOP 10,000 only.
/// Players outside the top 10k are not on the leaderboard and cannot be looked
/// up — every "not found" path therefore means "not ranked / outside top 10k"
/// (or no linked gamertag), NOT an error.
///
/// ── Caching ──
/// The full crossplay leaderboard (~10k rows) is fetched once and shared by every
/// surface (stale-while-revalidate, same shape as <see cref="UfcApiService"/>).
/// Reads never block on the network — slash handlers run on the gateway thread, so
/// a slow inline fetch there would starve Discord's heartbeat. An index keyed by
/// every (lower-cased, exact) Embark/Steam/PSN/Xbox name lets the board match the
/// whole roster against one in-memory snapshot.
///
/// ── No auth ──
/// The API needs no key, so the feature is always technically available; the
/// master switch is <see cref="BotConfig.FinalsEnabled"/>.
/// </summary>
public sealed class FinalsApiService
{
    private const string ApiBase = "https://api.the-finals-leaderboard.com";

    // The full leaderboard changes slowly relative to a clan's needs; an hour TTL
    // keeps the board/announcements fresh without hammering a community-hosted API.
    private static readonly TimeSpan LeaderboardTtl = TimeSpan.FromMinutes(60);
    // After an empty/failed fetch, retry sooner than a full TTL rather than serving
    // nothing for an hour. Good cached data is never clobbered by an empty result.
    private static readonly TimeSpan EmptyRetryTtl = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FinalsApiService> _logger;
    private readonly BotConfig _config;

    private readonly object _cacheLock = new();
    private List<FinalsEntry> _entries = new();
    private Dictionary<string, FinalsEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _expireUtc = DateTime.MinValue;
    private Task? _refreshTask;
    private volatile bool _loaded;

    public FinalsApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<FinalsApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>True once at least one leaderboard fetch has completed.</summary>
    public bool IsWarmedUp => _loaded;

    /// <summary>
    /// The cached leaderboard snapshot (stale-while-revalidate). Returns whatever
    /// is cached immediately and kicks off a background refresh if it's stale.
    /// </summary>
    public IReadOnlyList<FinalsEntry> Snapshot()
    {
        TriggerRefreshIfStale();
        lock (_cacheLock) { return _entries; }
    }

    /// <summary>
    /// Ensures the leaderboard cache is fresh, awaiting an in-flight refresh if one
    /// is running. For background services only — NEVER call from a slash-command
    /// handler (it can block on the network, which would starve the gateway). Errors
    /// are swallowed (the refresh logs its own); callers should check the snapshot.
    /// </summary>
    public async Task EnsureWarmAsync(CancellationToken ct = default)
    {
        TriggerRefreshIfStale();
        Task? t;
        lock (_cacheLock) { t = _refreshTask; }
        if (t is { IsCompleted: false })
        {
            try { await t.WaitAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* refresh logs its own failures; caller inspects the snapshot */ }
        }
    }

    /// <summary>
    /// Matches a member's roster gamertags against the cached leaderboard, trying
    /// Embark ID first (the API's primary key) then Steam, PSN, Xbox. Returns null
    /// if none of their handles is on the leaderboard (i.e. outside the top 10k).
    /// </summary>
    public FinalsRankResult? Match(GamertagLookupResult tags)
    {
        TriggerRefreshIfStale();

        Dictionary<string, FinalsEntry> index;
        lock (_cacheLock) { index = _index; }
        if (index.Count == 0) return null;

        // Order matters: Embark is the canonical in-game identity the API keys on,
        // so it's the most reliable; platform names are the fallback.
        foreach (var (via, value) in new[]
                 {
                     ("Embark", tags.Embark),
                     ("Steam",  tags.Steam),
                     ("PSN",    tags.PSN),
                     ("Xbox",   tags.Xbox),
                 })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (index.TryGetValue(value.Trim(), out var entry))
                return new FinalsRankResult(entry, via, value.Trim());
        }

        return null;
    }

    /// <summary>
    /// Live, on-demand name search against the API (case-insensitive substring
    /// across all of a player's names). Used by <c>/finals-rank name:</c>. Returns
    /// the matches ordered by rank (best first), capped at <paramref name="max"/>.
    /// </summary>
    public async Task<IReadOnlyList<FinalsEntry>> SearchByNameAsync(
        string query, int max = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<FinalsEntry>();

        var url = $"{ApiBase}/v1/leaderboard/{Version}/{Platform}?name={Uri.EscapeDataString(query.Trim())}";
        var resp = await FetchAsync(url, ct);
        if (resp is null) return Array.Empty<FinalsEntry>();

        return resp
            .OrderBy(e => e.Rank)
            .Take(max)
            .ToList();
    }

    private string Version => string.IsNullOrWhiteSpace(_config.FinalsLeaderboardVersion)
        ? "s10"
        : _config.FinalsLeaderboardVersion.Trim();

    private string Platform => string.IsNullOrWhiteSpace(_config.FinalsPlatform)
        ? "crossplay"
        : _config.FinalsPlatform.Trim();

    private void TriggerRefreshIfStale()
    {
        lock (_cacheLock)
        {
            if (DateTime.UtcNow < _expireUtc) return;            // still fresh
            if (_refreshTask is { IsCompleted: false }) return;  // already refreshing
            _refreshTask = Task.Run(RefreshAsync);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var url = $"{ApiBase}/v1/leaderboard/{Version}/{Platform}";
            var entries = await FetchAsync(url, CancellationToken.None);

            lock (_cacheLock)
            {
                if (entries is { Count: > 0 })
                {
                    _entries = entries;
                    _index = BuildIndex(entries);
                    _expireUtc = DateTime.UtcNow.Add(LeaderboardTtl);
                    _logger.LogInformation(
                        "FinalsApiService cached {Count} leaderboard entries for {Version}/{Platform}",
                        entries.Count, Version, Platform);
                }
                else
                {
                    // Keep whatever we had; retry sooner than a full TTL.
                    _expireUtc = DateTime.UtcNow.Add(EmptyRetryTtl);
                }
                _loaded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FinalsApiService background refresh failed");
            lock (_cacheLock) { _expireUtc = DateTime.UtcNow.Add(EmptyRetryTtl); }
        }
    }

    /// <summary>
    /// Builds the lookup index. Each non-empty Embark/Steam/PSN/Xbox name maps to
    /// its entry. On the rare collision (two players sharing a platform name) the
    /// better-ranked entry wins, since the list is processed rank-ascending.
    /// </summary>
    private static Dictionary<string, FinalsEntry> BuildIndex(List<FinalsEntry> entries)
    {
        var index = new Dictionary<string, FinalsEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.OrderBy(e => e.Rank))
        {
            foreach (var key in new[] { e.Name, e.SteamName, e.PsnName, e.XboxName })
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                index.TryAdd(key.Trim(), e);
            }
        }
        return index;
    }

    private async Task<List<FinalsEntry>?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("THE FINALS API returned {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<ApiResponse>(json, JsonOpts);
            if (parsed?.Data is null) return null;

            var result = new List<FinalsEntry>(parsed.Data.Count);
            foreach (var d in parsed.Data)
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                result.Add(new FinalsEntry(
                    Rank: d.Rank,
                    Change: d.Change,
                    Name: d.Name!,
                    SteamName: d.SteamName ?? "",
                    PsnName: d.PsnName ?? "",
                    XboxName: d.XboxName ?? "",
                    ClubTag: d.ClubTag ?? "",
                    LeagueNumber: d.LeagueNumber,
                    League: d.League ?? "",
                    RankScore: d.RankScore));
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "THE FINALS API fetch failed for {Url}", url);
            return null;
        }
    }

    // ─── API DTOs (defensive: everything nullable) ───────────────────────────

    private sealed class ApiResponse
    {
        public List<ApiEntry>? Data { get; set; }
    }

    private sealed class ApiEntry
    {
        public int Rank { get; set; }
        public int Change { get; set; }
        public string? Name { get; set; }
        public string? SteamName { get; set; }
        public string? PsnName { get; set; }
        public string? XboxName { get; set; }
        public string? ClubTag { get; set; }
        public int LeagueNumber { get; set; }
        public string? League { get; set; }
        public int RankScore { get; set; }
    }
}
