using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>Host-level view of the game server from Nitrado's control-panel API.</summary>
/// <param name="Status">Raw Nitrado status: started / restarting / stopped / updating / suspended …</param>
/// <param name="GameHuman">Human game name, e.g. "Satisfactory".</param>
/// <param name="Version">Installed game build/version, from the host query (may be empty).</param>
/// <param name="PlayerCurrent">Players per Nitrado's query — UNRELIABLE for Satisfactory; prefer the game API's count.</param>
/// <param name="PlayerMax">Slot-based max players.</param>
/// <param name="Slots">Purchased player slots.</param>
/// <param name="UpdateStatus">game_specific.update_status, e.g. "up_to_date" / "updating".</param>
public sealed record NitradoGameServer(
    string Status,
    string GameHuman,
    string Version,
    int PlayerCurrent,
    int PlayerMax,
    int Slots,
    string UpdateStatus);

/// <summary>Billing/rental view of the service.</summary>
/// <param name="SuspendDate">When the rental lapses (server stops) unless renewed. Null if unknown.</param>
/// <param name="AutoExtension">True if the service auto-renews — no expiry warning needed.</param>
public sealed record NitradoService(
    DateTimeOffset? SuspendDate,
    bool AutoExtension);

/// <summary>
/// Read-only client for the Nitrado control-panel API (api.nitrado.net), used to
/// AUGMENT the Satisfactory feature with things the game's own API can't report:
/// host-level status (so a planned restart reads differently from a crash), the
/// installed game build, and the rental expiry date.
///
/// ── Scope: reads only ──
/// Deliberately no start/stop/restart here. The clan asked for smarter status and
/// server info, not remote power control, and keeping it read-only means the token
/// can't be used to take the server down.
///
/// ── Auth ──
/// A single Bearer access token (<see cref="BotConfig.NitradoAccessToken"/>) issued
/// from a Nitrado account with access to the service. Unlike the game API this is a
/// normal public host with a valid TLS cert, so no cert-bypass is needed — it uses
/// the default HttpClient.
///
/// ── Service id ──
/// Every call targets one service id. It's taken from config, or auto-discovered
/// once by listing the token's services and picking the Satisfactory game server,
/// then cached for the process lifetime.
///
/// ── Failure semantics ──
/// Every read returns null on ANY failure (offline, timeout, 401, bad JSON) and
/// never throws. Callers treat null as "couldn't reach Nitrado" and fall back to the
/// game API, so a Nitrado outage never blinds the feature.
/// </summary>
public sealed class NitradoApiService
{
    private const string BaseUrl = "https://api.nitrado.net";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // Nitrado's JSON is snake_case; map it to PascalCase DTOs via the naming policy.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NitradoApiService> _logger;
    private readonly BotConfig _config;

    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private long? _resolvedServiceId;

    public NitradoApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<NitradoApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>True when an access token is present. Enable flag is checked by callers.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.NitradoAccessToken);

    // ─── Reads ───────────────────────────────────────────────────────────────

    /// <summary>Host status + game build for the Satisfactory service. Null = Nitrado unreachable.</summary>
    public async Task<NitradoGameServer?> GetGameServerAsync(CancellationToken ct = default)
    {
        var id = await ResolveServiceIdAsync(ct);
        if (id is null) return null;

        var resp = await GetAsync<GameServerResp>($"/services/{id}/gameservers", ct);
        var gs = resp?.Data?.Gameserver;
        if (gs is null) return null;

        return new NitradoGameServer(
            Status: gs.Status ?? "",
            GameHuman: gs.GameHuman ?? "",
            Version: gs.Query?.Version ?? "",
            PlayerCurrent: gs.Query?.PlayerCurrent ?? 0,
            PlayerMax: gs.Query?.PlayerMax ?? gs.Slots,
            Slots: gs.Slots,
            UpdateStatus: gs.GameSpecific?.UpdateStatus ?? "");
    }

    /// <summary>Rental/billing info (expiry, auto-renew). Null = Nitrado unreachable.</summary>
    public async Task<NitradoService?> GetServiceAsync(CancellationToken ct = default)
    {
        var id = await ResolveServiceIdAsync(ct);
        if (id is null) return null;

        var resp = await GetAsync<ServiceDetailResp>($"/services/{id}", ct);
        var svc = resp?.Data?.Service;
        if (svc is null) return null;

        return new NitradoService(
            SuspendDate: ParseDate(svc.SuspendDate),
            AutoExtension: svc.AutoExtension);
    }

    // ─── Service-id resolution ─────────────────────────────────────────────────

    /// <summary>
    /// The configured service id, or the auto-discovered Satisfactory service, cached
    /// after the first success. Serialized so a burst of calls discovers at most once.
    /// </summary>
    private async Task<long?> ResolveServiceIdAsync(CancellationToken ct)
    {
        if (_config.NitradoServiceId > 0) return _config.NitradoServiceId;
        if (_resolvedServiceId is not null) return _resolvedServiceId;

        await _resolveLock.WaitAsync(ct);
        try
        {
            if (_resolvedServiceId is not null) return _resolvedServiceId;

            var resp = await GetAsync<ServicesResp>("/services", ct);
            var services = resp?.Data?.Services;
            if (services is null || services.Count == 0)
            {
                _logger.LogWarning("Nitrado: /services returned nothing — can't auto-discover the service id (set NitradoServiceId)");
                return null;
            }

            bool IsSatisfactory(ServiceDto s) =>
                (s.Details?.Game ?? "").Contains("satisf", StringComparison.OrdinalIgnoreCase)
                || (s.Details?.FolderShort ?? "").Contains("satisf", StringComparison.OrdinalIgnoreCase)
                || (s.Details?.Name ?? "").Contains("satisf", StringComparison.OrdinalIgnoreCase);

            var match = services.FirstOrDefault(IsSatisfactory)
                        // Fall back to the only gameserver on the account, if there's exactly one.
                        ?? (services.Count(s => string.Equals(s.Type, "gameserver", StringComparison.OrdinalIgnoreCase)) == 1
                            ? services.First(s => string.Equals(s.Type, "gameserver", StringComparison.OrdinalIgnoreCase))
                            : null);

            if (match is null)
            {
                _logger.LogWarning(
                    "Nitrado: couldn't identify the Satisfactory service among {Count} services — set NitradoServiceId explicitly",
                    services.Count);
                return null;
            }

            _resolvedServiceId = match.Id;
            _logger.LogInformation("Nitrado: using service id {ServiceId} ({Name})", match.Id, match.Details?.Name ?? match.Type);
            return _resolvedServiceId;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    // ─── Transport ───────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!IsConfigured) return null;

        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.NitradoAccessToken);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.GetAsync($"{BaseUrl}{path}", ct);

            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    _logger.LogError("Nitrado API 401 for {Path} — NitradoAccessToken is wrong, expired, or lacks access to the service", path);
                else
                    _logger.LogWarning("Nitrado API returned {Status} for {Path}", (int)resp.StatusCode, path);
                return null;
            }

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
            _logger.LogDebug(ex, "Nitrado GET {Path} failed", path);
            return null;
        }
    }

    private static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, out var d) ? d : null;

    // ─── API DTOs ──────────────────────────────────────────────────────────────

    private sealed class ServicesResp
    {
        public ServicesData? Data { get; set; }
    }

    private sealed class ServicesData
    {
        public List<ServiceDto>? Services { get; set; }
    }

    private sealed class ServiceDetailResp
    {
        public ServiceData? Data { get; set; }
    }

    private sealed class ServiceData
    {
        public ServiceDto? Service { get; set; }
    }

    private sealed class ServiceDto
    {
        public long Id { get; set; }
        public string? Status { get; set; }
        public string? Type { get; set; }
        public bool AutoExtension { get; set; }
        public string? SuspendDate { get; set; }
        public ServiceDetailsDto? Details { get; set; }
    }

    private sealed class ServiceDetailsDto
    {
        public string? Name { get; set; }
        public string? Game { get; set; }
        public string? FolderShort { get; set; }
    }

    private sealed class GameServerResp
    {
        public GameServerData? Data { get; set; }
    }

    private sealed class GameServerData
    {
        public GameServerDto? Gameserver { get; set; }
    }

    private sealed class GameServerDto
    {
        public string? Status { get; set; }
        public string? Game { get; set; }
        public string? GameHuman { get; set; }
        public int Slots { get; set; }
        public GameSpecificDto? GameSpecific { get; set; }
        public QueryDto? Query { get; set; }
    }

    private sealed class GameSpecificDto
    {
        public string? UpdateStatus { get; set; }
    }

    private sealed class QueryDto
    {
        public string? ServerName { get; set; }
        public string? Version { get; set; }
        public int PlayerCurrent { get; set; }
        public int PlayerMax { get; set; }
    }
}
