using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>One player currently connected to the Palworld server (from /v1/api/players).</summary>
/// <param name="Name">In-game character name.</param>
/// <param name="AccountName">Platform account name (Steam/Xbox/PS).</param>
/// <param name="PlayerId">Per-character id. NOT stable across character re-creation.</param>
/// <param name="UserId">Platform user id (e.g. "steam_0110000…"). The stable identity — key on this.</param>
public sealed record PalworldPlayer(
    string Name,
    string AccountName,
    string PlayerId,
    string UserId,
    int Ping,
    int Level,
    int BuildingCount);

/// <summary>Server health snapshot (from /v1/api/metrics).</summary>
public sealed record PalworldMetrics(
    int ServerFps,
    int CurrentPlayerNum,
    int MaxPlayerNum,
    double ServerFrameTime,
    int Uptime,
    int BaseCampNum,
    int Days);

/// <summary>Server identity (from /v1/api/info).</summary>
public sealed record PalworldInfo(string Version, string ServerName, string Description);

/// <summary>
/// Client for the Palworld dedicated server's built-in REST API — the clan's
/// server is hosted on DatHost (flywheel.dathost.net), where the API is turned on
/// with the panel's "Enable REST API" toggle and lands on its own allocated port
/// alongside the game/query/RCON ports.
///
/// ── Why REST and not RCON ──
/// Pocketpair has deprecated RCON and says it will stop working in a future
/// update. The REST API is the supported successor and exposes strictly more
/// (player levels, base counts, server FPS), so everything here goes through it.
/// There is no RCON code path on purpose.
///
/// ── Auth ──
/// HTTP Basic, username literally "admin", password = the server's Admin Password
/// (<see cref="BotConfig.PalworldAdminPassword"/> — the RCON/REST credential, NOT
/// the joinable "Server Password"). That single credential grants kick/ban/save/
/// shutdown, so it is injected from the environment and never committed.
///
/// ── No push, no chat ──
/// The API has no webhooks and no event stream: presence is derived by polling
/// /players and diffing (see <see cref="PalworldPresenceService"/>). It also has no
/// game→Discord chat relay; only Discord→game, via <see cref="AnnounceAsync"/>.
///
/// ── Failure semantics ──
/// Every read returns null on ANY failure (server offline, timeout, 401, bad JSON)
/// and never throws, because a Palworld server being down is a routine state, not
/// an error. Callers MUST treat null as "unknown", not "nobody online" — the
/// presence poller depends on that distinction to avoid announcing a fake mass
/// exodus every time the server restarts. Writes return a bool.
/// </summary>
public sealed class PalworldApiService
{
    // The API mounts everything under /v1/api (see docs.palworldgame.com).
    private const string ApiPrefix = "/v1/api";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PalworldApiService> _logger;
    private readonly BotConfig _config;

    public PalworldApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<PalworldApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// True when both a base URL and an admin password are present. Everything in
    /// this feature is a no-op otherwise — same idle-until-configured shape as the
    /// UFC and FINALS features.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.PalworldBaseUrl)
        && !string.IsNullOrWhiteSpace(_config.PalworldAdminPassword);

    // ─── Reads ───────────────────────────────────────────────────────────────

    /// <summary>Players currently connected. Null = server unreachable (NOT "empty").</summary>
    public async Task<IReadOnlyList<PalworldPlayer>?> GetPlayersAsync(CancellationToken ct = default)
    {
        var dto = await GetAsync<PlayersDto>("players", ct);
        if (dto?.Players is null) return null;

        var result = new List<PalworldPlayer>(dto.Players.Count);
        foreach (var p in dto.Players)
        {
            // A player with no identity at all is unusable for diffing; skip it
            // rather than let a blank key collapse two players into one.
            if (string.IsNullOrWhiteSpace(p.UserId) && string.IsNullOrWhiteSpace(p.PlayerId))
                continue;

            result.Add(new PalworldPlayer(
                Name: string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name!,
                AccountName: p.AccountName ?? "",
                PlayerId: p.PlayerId ?? "",
                UserId: p.UserId ?? "",
                Ping: (int)Math.Round(p.Ping),
                Level: p.Level,
                BuildingCount: p.Building_Count));
        }
        return result;
    }

    /// <summary>Server metrics (FPS, uptime, in-game day…). Null = unreachable.</summary>
    public async Task<PalworldMetrics?> GetMetricsAsync(CancellationToken ct = default)
    {
        var dto = await GetAsync<MetricsDto>("metrics", ct);
        if (dto is null) return null;

        return new PalworldMetrics(
            ServerFps: dto.ServerFps,
            CurrentPlayerNum: dto.CurrentPlayerNum,
            MaxPlayerNum: dto.MaxPlayerNum,
            ServerFrameTime: dto.ServerFrameTime,
            Uptime: dto.Uptime,
            BaseCampNum: dto.BaseCampNum,
            Days: dto.Days);
    }

    /// <summary>Server name/version. Null = unreachable. Doubles as a connectivity probe.</summary>
    public async Task<PalworldInfo?> GetInfoAsync(CancellationToken ct = default)
    {
        var dto = await GetAsync<InfoDto>("info", ct);
        if (dto is null) return null;

        return new PalworldInfo(
            Version: dto.Version ?? "",
            ServerName: dto.ServerName ?? "",
            Description: dto.Description ?? "");
    }

    // ─── Writes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Broadcasts an in-game message to everyone connected. This is the ONLY
    /// Discord→game channel the API offers, so it's what the event-reminder bridge
    /// and <c>/palworld-announce</c> both use.
    /// </summary>
    public Task<bool> AnnounceAsync(string message, CancellationToken ct = default) =>
        PostAsync("announce", new { message = Sanitize(message) }, ct);

    /// <summary>Kicks a player by their platform user id (the <see cref="PalworldPlayer.UserId"/>).</summary>
    public Task<bool> KickAsync(string userId, string message, CancellationToken ct = default) =>
        PostAsync("kick", new { userid = userId, message = Sanitize(message) }, ct);

    /// <summary>Bans a player by their platform user id. Reversible with <see cref="UnbanAsync"/>.</summary>
    public Task<bool> BanAsync(string userId, string message, CancellationToken ct = default) =>
        PostAsync("ban", new { userid = userId, message = Sanitize(message) }, ct);

    public Task<bool> UnbanAsync(string userId, CancellationToken ct = default) =>
        PostAsync("unban", new { userid = userId }, ct);

    /// <summary>Forces a world save. Always worth calling before a shutdown.</summary>
    public Task<bool> SaveAsync(CancellationToken ct = default) =>
        PostAsync("save", null, ct);

    /// <summary>
    /// Graceful shutdown after <paramref name="waitSeconds"/>, broadcasting
    /// <paramref name="message"/> to connected players first. DatHost restarts the
    /// process automatically, so in practice this behaves as a restart.
    /// </summary>
    public Task<bool> ShutdownAsync(int waitSeconds, string message, CancellationToken ct = default) =>
        PostAsync("shutdown", new { waittime = Math.Clamp(waitSeconds, 1, 3600), message = Sanitize(message) }, ct);

    // ─── Transport ───────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!IsConfigured) return null;

        try
        {
            using var http = CreateClient();
            using var resp = await http.GetAsync(Url(path), ct);
            if (!await EnsureOkAsync(resp, path, ct)) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Server offline / DNS / timeout. Debug, not Error: an offline game
            // server is normal and this poller runs every minute — logging it at
            // Error would bury the log in noise overnight.
            _logger.LogDebug(ex, "Palworld GET {Path} failed (server offline?)", path);
            return null;
        }
    }

    private async Task<bool> PostAsync(string path, object? body, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Palworld POST {Path} skipped — PalworldBaseUrl/PalworldAdminPassword not set", path);
            return false;
        }

        try
        {
            using var http = CreateClient();

            // Every write endpoint takes a JSON body; the parameterless ones (/save)
            // still want a valid JSON object rather than an empty payload.
            var payload = JsonSerializer.Serialize(body ?? new { });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await http.PostAsync(Url(path), content, ct);
            return await EnsureOkAsync(resp, path, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Writes ARE worth an error-level log: someone ran a command and it
            // silently did nothing.
            _logger.LogError(ex, "Palworld POST {Path} failed", path);
            return false;
        }
    }

    /// <summary>
    /// Logs a non-2xx response distinctly enough to be actionable. 401 is called
    /// out by name because it means the Admin Password is wrong or was rotated on
    /// the DatHost panel without updating the bot's env — by far the most likely
    /// misconfiguration, and one that otherwise looks like "server offline".
    /// </summary>
    private async Task<bool> EnsureOkAsync(HttpResponseMessage resp, string path, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return true;

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogError(
                "Palworld API returned 401 for {Path} — BotConfig__PalworldAdminPassword does not match the " +
                "server's Admin Password (check the DatHost Settings tab; note it is NOT the joinable Server Password)",
                path);
            return false;
        }

        var body = await SafeReadAsync(resp, ct);
        _logger.LogWarning("Palworld API returned {Status} for {Path}: {Body}",
            (int)resp.StatusCode, path, Truncate(body, 200));
        return false;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private HttpClient CreateClient()
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = RequestTimeout;

        // Basic auth: the username is the literal string "admin" for every Palworld
        // server — only the password varies.
        var raw = Encoding.UTF8.GetBytes($"admin:{_config.PalworldAdminPassword}");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

        return http;
    }

    private string Url(string path) =>
        $"{_config.PalworldBaseUrl.TrimEnd('/')}{ApiPrefix}/{path}";

    /// <summary>
    /// In-game announcements are plain text with no markup support, and a newline
    /// can truncate the broadcast. Flatten to a single line and cap the length.
    /// </summary>
    private static string Sanitize(string message)
    {
        var flat = (message ?? "")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
        return Truncate(flat, 200);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    // ─── API DTOs (defensive: everything nullable) ───────────────────────────

    private sealed class PlayersDto
    {
        public List<PlayerDto>? Players { get; set; }
    }

    private sealed class PlayerDto
    {
        public string? Name { get; set; }
        public string? AccountName { get; set; }
        public string? PlayerId { get; set; }
        public string? UserId { get; set; }
        public string? Ip { get; set; }
        public double Ping { get; set; }
        public double Location_X { get; set; }
        public double Location_Y { get; set; }
        public int Level { get; set; }
        public int Building_Count { get; set; }
    }

    private sealed class MetricsDto
    {
        public int ServerFps { get; set; }
        public int CurrentPlayerNum { get; set; }
        public int MaxPlayerNum { get; set; }
        public double ServerFrameTime { get; set; }
        public int Uptime { get; set; }
        public int BaseCampNum { get; set; }
        public int Days { get; set; }
    }

    private sealed class InfoDto
    {
        public string? Version { get; set; }
        public string? ServerName { get; set; }
        public string? Description { get; set; }
        public string? WorldGuid { get; set; }
    }
}
