using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>Live state of the Dedicated Server (from QueryServerState → serverGameState).</summary>
/// <param name="ActiveSessionName">Name of the currently loaded save/session.</param>
/// <param name="NumConnectedPlayers">Players currently connected. The API exposes only this COUNT — never names.</param>
/// <param name="PlayerLimit">Max players allowed.</param>
/// <param name="TechTier">Highest tech tier unlocked.</param>
/// <param name="GamePhase">Raw game-phase asset path, or "None" if no game is running.</param>
/// <param name="IsGameRunning">True once a save is loaded (false while waiting for a session).</param>
/// <param name="TotalGameDurationSeconds">Seconds the current save has been loaded.</param>
/// <param name="IsGamePaused">True if the game is paused (duration stops advancing).</param>
/// <param name="AverageTickRate">Server tick rate. Below ~10 tps the server reports itself "slow".</param>
public sealed record SatisfactoryServerState(
    string ActiveSessionName,
    int NumConnectedPlayers,
    int PlayerLimit,
    int TechTier,
    string GamePhase,
    bool IsGameRunning,
    int TotalGameDurationSeconds,
    bool IsGamePaused,
    double AverageTickRate);

/// <summary>
/// Client for the Satisfactory Dedicated Server's built-in HTTPS API (the one that
/// shipped with the 1.0 release — no mods required).
///
/// ── Endpoint ──
/// Everything is a POST to <c>{BaseUrl}/api/v1</c> carrying a JSON envelope
/// <c>{ "function": "...", "data": {...} }</c>. Crucially the API rides the SAME
/// port players connect to in-game (7777 by default), so the IP:port the clan types
/// into the game IS the base URL — just prefixed with https.
///
/// ── TLS / self-signed cert ──
/// The API is always TLS, and vanilla servers present a SELF-SIGNED certificate.
/// A normal HttpClient would reject it, so this feature uses a named HttpClient
/// ("satisfactory") whose primary handler accepts any server cert — configured once
/// in Program.cs. That trust is scoped to this one client; the rest of the bot's
/// HTTP calls keep full validation.
///
/// ── Auth ──
/// Bearer tokens only. Two ways in, in priority order:
///   1. A pre-generated Application token (<see cref="BotConfig.SatisfactoryApiToken"/>),
///      made with `server.GenerateAPIToken` on the server console. Never expires,
///      so it is used verbatim with no login round-trip.
///   2. The Admin Password (<see cref="BotConfig.SatisfactoryAdminPassword"/>),
///      exchanged for a short-lived token via PasswordLogin. That token is cached
///      and transparently refreshed on a 401.
///
/// ── Player names are NOT available ──
/// This API has no player-list function. QueryServerState returns a
/// player COUNT and nothing that identifies who is on. There is therefore no
/// per-player join/leave feed, no playtime tracking, and no link command — those
/// simply cannot be built on this API.
///
/// ── Failure semantics ──
/// Every read returns null on ANY failure (offline, timeout, 401, bad JSON) and never
/// throws, because a game server being down is a routine state. Callers MUST treat
/// null as "unknown", not "nobody online" — the presence poller depends on that
/// distinction to avoid announcing a fake outage every restart. Writes return a bool.
/// </summary>
public sealed class SatisfactoryApiService
{
    /// <summary>Name of the cert-trusting HttpClient registered in Program.cs.</summary>
    public const string HttpClientName = "satisfactory";

    // The API mounts everything under /api/v1.
    private const string ApiPath = "/api/v1";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SatisfactoryApiService> _logger;
    private readonly BotConfig _config;

    // Cached Bearer token from a PasswordLogin, guarded so two concurrent calls can't
    // both log in. Null when we have none yet. Unused when a static API token is set.
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;

    public SatisfactoryApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<SatisfactoryApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>True when a base URL and at least one credential (token or password) are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.SatisfactoryBaseUrl)
        && (!string.IsNullOrWhiteSpace(_config.SatisfactoryApiToken)
            || !string.IsNullOrWhiteSpace(_config.SatisfactoryAdminPassword));

    /// <summary>True when a fixed, never-expiring Application token is configured.</summary>
    private bool UsesStaticToken => !string.IsNullOrWhiteSpace(_config.SatisfactoryApiToken);

    // ─── Reads ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight, no-auth health probe. Returns "healthy" (tick rate above ~10 tps)
    /// or "slow"; null when the server is unreachable. Good as a pure connectivity
    /// check that never touches the credential.
    /// </summary>
    public async Task<string?> GetHealthAsync(CancellationToken ct = default)
    {
        var result = await InvokeAsync("HealthCheck", new { ClientCustomData = "" }, requiresAuth: false, ct);
        if (!result.Ok || result.Data is null) return null;

        var dto = Deserialize<HealthDto>(result.Data.Value);
        return string.IsNullOrWhiteSpace(dto?.Health) ? null : dto!.Health;
    }

    /// <summary>Current server state (players, session, phase, tick rate…). Null = unreachable.</summary>
    public async Task<SatisfactoryServerState?> GetServerStateAsync(CancellationToken ct = default)
    {
        var result = await InvokeAsync("QueryServerState", data: null, requiresAuth: true, ct);
        if (!result.Ok || result.Data is null) return null;

        // Response shape is { "data": { "serverGameState": { ... } } }. InvokeAsync
        // already unwrapped "data"; dig one more level for serverGameState.
        if (!TryGetProp(result.Data.Value, "serverGameState", out var state)) return null;

        var dto = Deserialize<ServerStateDto>(state);
        if (dto is null) return null;

        return new SatisfactoryServerState(
            ActiveSessionName: dto.ActiveSessionName ?? "",
            NumConnectedPlayers: dto.NumConnectedPlayers,
            PlayerLimit: dto.PlayerLimit,
            TechTier: dto.TechTier,
            GamePhase: dto.GamePhase ?? "None",
            IsGameRunning: dto.IsGameRunning,
            TotalGameDurationSeconds: dto.TotalGameDuration,
            IsGamePaused: dto.IsGamePaused,
            AverageTickRate: dto.AverageTickRate);
    }

    // ─── Writes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the currently loaded session to a named save file. The server may sanitize
    /// the name to satisfy filesystem rules. Requires Admin privileges.
    /// </summary>
    public async Task<bool> SaveGameAsync(string saveName, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(saveName) ? "ClanSave" : saveName.Trim();
        var result = await InvokeAsync("SaveGame", new { SaveName = name }, requiresAuth: true, ct);
        return result.Ok;
    }

    /// <summary>
    /// Shuts the server process down. If the host has a restart script (DatHost,
    /// systemd, a Docker restart policy…) this behaves as a restart. Requires Admin.
    /// </summary>
    public async Task<bool> ShutdownAsync(CancellationToken ct = default)
    {
        var result = await InvokeAsync("Shutdown", data: null, requiresAuth: true, ct);
        return result.Ok;
    }

    /// <summary>
    /// Runs a console command on the server and returns its console output. Requires
    /// Admin. Vanilla servers expose only a limited command set, so many commands are
    /// no-ops — this is here for the handful that work (e.g. FG.* cvars).
    /// </summary>
    public async Task<(bool Ok, string? Output)> RunCommandAsync(string command, CancellationToken ct = default)
    {
        var result = await InvokeAsync("RunCommand", new { Command = command }, requiresAuth: true, ct);
        if (!result.Ok) return (false, null);
        if (result.Data is null) return (true, null);

        var dto = Deserialize<RunCommandDto>(result.Data.Value);
        return (true, dto?.CommandResult);
    }

    // ─── Transport ───────────────────────────────────────────────────────────

    private readonly record struct ApiResult(bool Ok, JsonElement? Data, string? ErrorCode);

    /// <summary>
    /// The single request pipeline. Wraps the function+data envelope, applies auth,
    /// and normalizes every outcome into an <see cref="ApiResult"/>: it never throws
    /// for a server-side or network failure. On a 401 with password auth it refreshes
    /// the token and retries exactly once.
    /// </summary>
    private async Task<ApiResult> InvokeAsync(
        string function, object? data, bool requiresAuth, CancellationToken ct, bool isRetry = false)
    {
        if (!IsConfigured)
        {
            if (requiresAuth)
                _logger.LogWarning("Satisfactory {Function} skipped — base URL/credential not configured", function);
            return new ApiResult(false, null, null);
        }

        try
        {
            using var http = _httpClientFactory.CreateClient(HttpClientName);
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            if (requiresAuth)
            {
                var token = await GetTokenAsync(ct, forceRefresh: false);
                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogWarning("Satisfactory {Function}: could not obtain an auth token", function);
                    return new ApiResult(false, null, null);
                }
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var payload = JsonSerializer.Serialize(new { function, data = data ?? new { } });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await http.PostAsync(Url(), content, ct);

            // 401 with a refreshable (password) token: log back in once and retry.
            if (resp.StatusCode == HttpStatusCode.Unauthorized && requiresAuth && !UsesStaticToken && !isRetry)
            {
                _logger.LogInformation("Satisfactory {Function} got 401; refreshing token and retrying", function);
                InvalidateToken();
                await GetTokenAsync(ct, forceRefresh: true);
                return await InvokeAsync(function, data, requiresAuth, ct, isRetry: true);
            }

            return await ParseAsync(resp, function, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline / DNS / TLS / timeout. Debug (not Error): an offline game server
            // is normal, and the poller runs every minute — Error would bury the log.
            _logger.LogDebug(ex, "Satisfactory {Function} failed (server offline?)", function);
            return new ApiResult(false, null, null);
        }
    }

    private async Task<ApiResult> ParseAsync(HttpResponseMessage resp, string function, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogError(
                "Satisfactory API returned 401 for {Function} — the admin password/API token is wrong or the " +
                "token expired (check BotConfig__SatisfactoryAdminPassword / SatisfactoryApiToken)", function);
            return new ApiResult(false, null, null);
        }

        // 204 No Content: succeeded, nothing to parse (Shutdown, SaveGame on success…).
        if (resp.StatusCode == HttpStatusCode.NoContent)
            return new ApiResult(true, null, null);

        var body = await SafeReadAsync(resp, ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Satisfactory API returned {Status} for {Function}: {Body}",
                (int)resp.StatusCode, function, Truncate(body, 300));
            return new ApiResult(false, null, null);
        }

        if (string.IsNullOrWhiteSpace(body))
            return new ApiResult(true, null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Error responses come back 200-with-body: { errorCode, errorMessage }.
            if (TryGetProp(root, "errorCode", out var errCode))
            {
                var code = errCode.ValueKind == JsonValueKind.String ? errCode.GetString() : errCode.ToString();
                var msg = TryGetProp(root, "errorMessage", out var m) ? m.GetString() : null;
                _logger.LogWarning("Satisfactory API error for {Function}: {Code} — {Message}", function, code, msg);
                return new ApiResult(false, null, code);
            }

            // Success payloads carry their result under "data"; clone it out of the
            // JsonDocument so it survives disposal.
            if (TryGetProp(root, "data", out var dataEl))
                return new ApiResult(true, dataEl.Clone(), null);

            return new ApiResult(true, null, null);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Satisfactory API returned unparseable JSON for {Function}: {Body}",
                function, Truncate(body, 300));
            return new ApiResult(false, null, null);
        }
    }

    /// <summary>
    /// Returns a usable Bearer token: the static Application token verbatim, or a
    /// cached/freshly-issued PasswordLogin token. Serialized so a burst of calls
    /// triggers at most one login.
    /// </summary>
    private async Task<string?> GetTokenAsync(CancellationToken ct, bool forceRefresh)
    {
        if (UsesStaticToken) return _config.SatisfactoryApiToken;

        if (!forceRefresh && _cachedToken is not null) return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _cachedToken is not null) return _cachedToken;

            var login = await InvokeAsync(
                "PasswordLogin",
                new { MinimumPrivilegeLevel = "Administrator", Password = _config.SatisfactoryAdminPassword },
                requiresAuth: false,
                ct);

            if (!login.Ok || login.Data is null)
            {
                _cachedToken = null;
                return null;
            }

            var dto = Deserialize<LoginDto>(login.Data.Value);
            _cachedToken = string.IsNullOrWhiteSpace(dto?.AuthenticationToken) ? null : dto!.AuthenticationToken;

            if (_cachedToken is null)
                _logger.LogWarning("Satisfactory PasswordLogin succeeded but returned no token");

            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateToken()
    {
        if (!UsesStaticToken) _cachedToken = null;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private string Url() => $"{_config.SatisfactoryBaseUrl.TrimEnd('/')}{ApiPath}";

    private static T? Deserialize<T>(JsonElement el) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(el.GetRawText(), JsonOpts); }
        catch { return null; }
    }

    /// <summary>Case-insensitive property lookup — the API's JSON casing varies by function.</summary>
    private static bool TryGetProp(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in obj.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    // ─── API DTOs (defensive: everything nullable / defaulted) ─────────────────

    private sealed class HealthDto
    {
        public string? Health { get; set; }
        public string? ServerCustomData { get; set; }
    }

    private sealed class LoginDto
    {
        public string? AuthenticationToken { get; set; }
    }

    private sealed class RunCommandDto
    {
        public string? CommandResult { get; set; }
        public bool ReturnValue { get; set; }
    }

    private sealed class ServerStateDto
    {
        public string? ActiveSessionName { get; set; }
        public int NumConnectedPlayers { get; set; }
        public int PlayerLimit { get; set; }
        public int TechTier { get; set; }
        public string? ActiveSchematic { get; set; }
        public string? GamePhase { get; set; }
        public bool IsGameRunning { get; set; }
        public int TotalGameDuration { get; set; }
        public bool IsGamePaused { get; set; }
        public double AverageTickRate { get; set; }
        public string? AutoLoadSessionName { get; set; }
    }
}
