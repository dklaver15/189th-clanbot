using System.Text;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

// ─── Response models ────────────────────────────────────────────────────────
//
// These are modelled against the ACTUAL payloads returned by FRM 1.5.2 on the
// clan's server (captured 2026-07-25), not against docs.ficsit.app — the
// published schema is out of date in several places. Known divergences:
//   • getSessionInfo returns SpaceElevatorCost / RecipeCost / PowerCost /
//     NodeRando / NodePurity, none of which are documented.
//   • getPower's key is CircuitGroupID (the docs say CircuitID), and it also
//     returns BatteryInput/Output/Capacity/TimeFull and AssociatedCircuits.
//   • getModList returns SupportURL where the docs show AcceptsAnyRemoteVersion.
// Everything is nullable/defaulted so an unexpected shape degrades to a missing
// value rather than an exception.

/// <summary>World/session state. One object, not an array.</summary>
public sealed record FrmSessionInfo(
    string SessionName,
    bool IsPaused,
    int PassedDays,
    int NumberOfDaysSinceLastDeath,
    int Hours,
    int Minutes,
    bool IsDay,
    int TotalPlayDuration,
    string TotalPlayDurationText,
    long Seed,
    string NodeRando,
    string NodePurity);

/// <summary>A world position. Note the JSON key is lowercase "location".</summary>
public sealed record FrmLocation(double X, double Y, double Z, double Rotation);

/// <summary>
/// One player. This is the whole reason FRM is worth wiring up: the vanilla
/// Dedicated Server API exposes only a player COUNT, never a name.
///
/// <para><b>Identity:</b> <paramref name="Id"/> is the character actor id
/// ("Char_Player_C_2147473237") and is stable for the life of a save.
/// <paramref name="Name"/> is the Steam/Epic username ("Brandt") — recognisable
/// but user-editable, so it's a display label, not a key. A save wipe resets ids.</para>
///
/// <para><b>Offline players are included.</b> getPlayer returns every player the
/// save knows about, with <paramref name="Online"/> false for absent ones — so
/// callers MUST filter on Online rather than treating the array length as a
/// player count.</para>
/// </summary>
public sealed record FrmPlayer(
    string Id,
    string Name,
    bool Online,
    bool Dead,
    double PlayerHP,
    FrmLocation? Location);

/// <summary>
/// One power circuit group. <see cref="FuseTriggered"/> is the alert-worthy
/// field; <see cref="BatteryTimeEmpty"/> is an "HH:MM:SS" STRING, not a number.
/// </summary>
public sealed record FrmPowerCircuit(
    int CircuitGroupId,
    double PowerProduction,
    double PowerConsumed,
    double PowerCapacity,
    double PowerMaxConsumed,
    double BatteryPercent,
    double BatteryCapacity,
    string BatteryTimeEmpty,
    string BatteryTimeFull,
    bool FuseTriggered,
    IReadOnlyList<int>? AssociatedCircuits = null);

/// <summary>
/// The power block that hangs off every powered thing FRM reports — generators
/// and consumers alike. JSON key is "PowerInfo".
///
/// <para><b>CircuitGroupId is -1</b> for anything not wired to a circuit at all
/// (an unconnected pipeline junction, for one), so it is not safe to treat as
/// an index. Filter on equality with a known group, never on "not zero".</para>
///
/// <para>Note which field survives a trip: once the fuse blows, everything on
/// the circuit reads <see cref="PowerConsumed"/> ≈ 0, so post-trip only
/// <see cref="MaxPowerConsumed"/> still says anything about what was drawing.</para>
/// </summary>
public sealed record FrmPowerInfo(
    int CircuitGroupId,
    int CircuitId,
    bool FuseTriggered,
    double PowerConsumed,
    double MaxPowerConsumed);

/// <summary>
/// A generator's secondary input — water, for coal and fuel generators. Null on
/// generator types that don't take one.
///
/// <para><see cref="PercentFull"/> is the one worth alerting on: a coal plant
/// with no water produces nothing regardless of how much coal it's holding.</para>
/// </summary>
public sealed record FrmGeneratorSupplement(
    string? Name,
    string? ClassName,
    double CurrentConsumed,
    double MaxConsumed,
    double PercentFull);

/// <summary>One stack in a generator's fuel inventory.</summary>
public sealed record FrmFuelStack(
    string? Name,
    string? ClassName,
    double Amount,
    double MaxAmount);

/// <summary>
/// One power generator.
///
/// <para><b>Field names come from the live server, not the docs</b> — the docs
/// list IsFullBlast and a top-level CircuitID, neither of which exists in the
/// response. Both would have deserialized to a silent default.</para>
///
/// <para><see cref="FuelAmount"/> is a FRACTION (0.54 in the capture), i.e. how
/// far through the current fuel item the generator is — not a quantity, and not
/// a shortage signal. For "is it about to run dry", read
/// <see cref="FuelInventory"/> amounts instead.</para>
/// </summary>
public sealed record FrmGenerator(
    string? Id,
    string? Name,
    string? ClassName,
    double BaseProd,
    double ProductionCapacity,
    double PowerProductionPotential,
    double LoadPercentage,
    bool IsFullSpeed,
    bool CanStart,
    double FuelAmount,
    string? FuelResource,
    string? NuclearWarning,
    FrmGeneratorSupplement? Supplement,
    IReadOnlyList<FrmFuelStack>? FuelInventory,
    FrmPowerInfo? PowerInfo);

/// <summary>
/// One powered building, from getPowerUsage.
///
/// <para><b>This endpoint returns EVERY powered building in the save</b>, so it
/// belongs on a one-off diagnostic and never in a poll loop.</para>
/// </summary>
public sealed record FrmPoweredBuilding(
    string? Id,
    string? Name,
    string? ClassName,
    FrmPowerInfo? PowerInfo);

/// <summary>One item still owed for the current space-elevator phase.</summary>
public sealed record FrmPhaseItem(string Name, int RemainingCost, int TotalCost);

/// <summary>
/// A space elevator and its current phase requirements.
///
/// <para><b>Unverified shape.</b> The clan's save returned an empty array on
/// 2026-07-25 (no elevator built yet, game phase 0), so this is modelled from
/// the docs alone — unlike every other type here. Treat a null/blank field as
/// expected until it's been seen with real data.</para>
/// </summary>
public sealed record FrmSpaceElevator(
    string Id,
    string Name,
    bool FullyUpgraded,
    bool UpgradeReady,
    IReadOnlyList<FrmPhaseItem> CurrentPhase);

/// <summary>
/// Per-item production and consumption.
///
/// <para>FRM also returns a <c>ProdPerMin</c> string
/// ("P: 14.8/ min - C: 16.950001/ min") which is display formatting, not data —
/// deliberately not modelled. Use <see cref="CurrentProd"/> /
/// <see cref="CurrentConsumed"/> for anything numeric.</para>
///
/// <para><b>MaxProd can be 0</b> for items that are only ever consumed (Leaves,
/// for one), so never divide by it without a guard.</para>
/// </summary>
public sealed record FrmProdStat(
    string Name,
    string ClassName,
    double ProdPercent,
    double ConsPercent,
    double CurrentProd,
    double MaxProd,
    double CurrentConsumed,
    double MaxConsumed,
    string Type);

/// <summary>
/// A schematic — a milestone, alternate recipe, tutorial step or event unlock.
///
/// <para><b>Recipes and Cost are deliberately not modelled.</b> Each schematic
/// embeds its full recipe list with every ingredient and product, which is what
/// makes getSchematics 1.1 MB for 575 entries on the clan's server. Leaving them
/// out means the deserializer skips those tokens instead of materializing tens
/// of thousands of objects we'd immediately discard.</para>
///
/// <para><b><see cref="Name"/> can be empty.</b> Confirmed on the live server:
/// <c>Schematic_XMassTree_C</c> reports <c>"Name": ""</c>. Anything displaying
/// this must fall back to the id.</para>
/// </summary>
public sealed record FrmSchematic(
    string Id,
    string Name,
    string ClassName,
    int TechTier,
    string Type,
    bool Purchased);

/// <summary>One M.A.M. research category ("Alien Megafauna", "Quartz", …).</summary>
public sealed record FrmResearchTree(string Name, IReadOnlyList<FrmResearchNode> Nodes);

/// <summary>
/// One M.A.M. research node. <see cref="State"/> is the completion signal —
/// observed values on the live server are "Purchased", "Available" and "Locked".
/// </summary>
public sealed record FrmResearchNode(
    string Id,
    string Name,
    string ClassName,
    string Category,
    string State,
    int TechTier);

/// <summary>AWESOME Sink totals. GraphPoints is a rolling 10-sample history.</summary>
public sealed record FrmResourceSink(
    string Name,
    int NumCoupon,
    double Percent,
    long TotalPoints,
    long PointsToCoupon,
    IReadOnlyList<long> GraphPoints);

/// <summary>
/// An installed mod. <see cref="SmrName"/> is the mod-repository id — the thing
/// a member types into Satisfactory Mod Manager — so it's the useful one for
/// "match the server's modpack", not the friendly Name.
/// </summary>
public sealed record FrmMod(
    string Name,
    string SmrName,
    string Version,
    string CreatedBy,
    bool RequiredOnRemote,
    string DocsUrl,
    string SupportUrl);

/// <summary>
/// Client for the Ficsit Remote Monitoring mod's own HTTP server.
///
/// ── Why this exists alongside <see cref="SatisfactoryApiService"/> ──
/// Satisfactory's built-in Dedicated Server API can't name players, list
/// production, or report power — it returns a bare connected-player count. FRM
/// exposes ~90 read endpoints that cover all of that. The two clients are
/// deliberately separate: different host:port, different transport, different
/// auth, different failure meaning.
///
/// ── Transport ──
/// Plain <c>GET {BaseUrl}/{endpoint}</c>, plain HTTP, JSON at the top level —
/// an array for most endpoints, a bare object for getSessionInfo. No envelope,
/// no TLS, so this uses the DEFAULT HttpClient; it does NOT need (and must not
/// use) the cert-bypass "satisfactory" client, which exists only for the game
/// API's self-signed certificate.
///
/// ── The port is the whole story ──
/// FRM's server defaults to 8080 and cannot bind it on shared hosting. On the
/// clan's indifferent broccoli box it took a support ticket to get one extra TCP
/// port published to the container (27052) before it would start at all. If this
/// service starts returning nothing, check that first: the mod logs
/// "Attempting to listen on port N" then "Bind failed" when the port is gone.
///
/// FRM ALSO exposes the same endpoints through the game port via the vanilla
/// API (<c>{"function":"frm","endpoint":"..."}</c>), which would need no extra
/// port — but that bridge doesn't register in 1.5.2 (returns
/// <c>bad_function</c>). If a future release fixes it, this client could switch
/// to that transport and the extra port becomes unnecessary.
///
/// ── Auth ──
/// Reads need none. The five write endpoints (sendChatMessage, createPing,
/// setEnabled, setSwitches, setModSetting) need FRM's own token in an
/// <c>X-FRM-Authorization</c> header — <see cref="BotConfig.FrmAuthToken"/>,
/// generated by the mod at load and stored in the server's GameUserSettings.ini.
/// The header is applied to every request. sendChatMessage is implemented
/// (see SendChatMessageAsync); the other four are not exposed yet.
///
/// ── Performance ──
/// FRM's own server runs OFF the game thread, so these calls are far cheaper
/// than the same data over the game-port bridge. That is not a licence to poll
/// getFactory or getAll — those walk the whole save.
///
/// ── Failure semantics ──
/// Every read returns null on ANY failure (offline, timeout, bad JSON) and never
/// throws. Null means "unknown", never "empty" — callers must not treat a failed
/// read as "nobody online", or a server restart looks like a mass exodus.
/// </summary>
public sealed class FrmApiService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,   // getPlayer's "location" is lowercase
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FrmApiService> _logger;
    private readonly BotConfig _config;

    /// <summary>So the missing-token warning is logged once, not per attempt.</summary>
    private bool _warnedMissingToken;

    public FrmApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<FrmApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>True when the feature is switched on and a base URL is set.</summary>
    public bool IsConfigured =>
        _config.FrmEnabled && !string.IsNullOrWhiteSpace(_config.FrmBaseUrl);

    // ─── Reads ───────────────────────────────────────────────────────────────

    /// <summary>Session/world state. Null = unreachable.</summary>
    public Task<FrmSessionInfo?> GetSessionInfoAsync(CancellationToken ct = default) =>
        GetObjectAsync<FrmSessionInfo>("getSessionInfo", ct);

    /// <summary>
    /// Every player the save knows about, online or not. Null = unreachable.
    /// Use <see cref="GetOnlinePlayersAsync"/> unless you specifically want the
    /// offline ones too.
    /// </summary>
    public Task<IReadOnlyList<FrmPlayer>?> GetPlayersAsync(CancellationToken ct = default) =>
        GetListAsync<FrmPlayer>("getPlayer", ct);

    /// <summary>
    /// Currently-connected players only. Null = unreachable (NOT "nobody on") —
    /// an empty list is the "nobody on" answer.
    /// </summary>
    public async Task<IReadOnlyList<FrmPlayer>?> GetOnlinePlayersAsync(CancellationToken ct = default)
    {
        var all = await GetPlayersAsync(ct);
        return all?.Where(p => p.Online).ToList();
    }

    /// <summary>Power circuit groups, including fuse and battery state. Null = unreachable.</summary>
    public Task<IReadOnlyList<FrmPowerCircuit>?> GetPowerAsync(CancellationToken ct = default) =>
        GetListAsync<FrmPowerCircuit>("getPower", ct);

    /// <summary>
    /// Every generator, with fuel, water and run state. Moderately large — for
    /// diagnosing a specific event, not for polling. Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmGenerator>?> GetGeneratorsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmGenerator>("getGenerators", ct);

    /// <summary>
    /// Every powered building and what it draws. <b>The whole save</b> — this is
    /// in the same weight class as getFactory and must never go in a poll loop.
    /// Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmPoweredBuilding>?> GetPowerUsageAsync(CancellationToken ct = default) =>
        GetListAsync<FrmPoweredBuilding>("getPowerUsage", ct);

    /// <summary>
    /// Space elevators and their phase requirements. An EMPTY list is normal and
    /// common — it means no elevator has been built yet, which is the clan's
    /// current state. Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmSpaceElevator>?> GetSpaceElevatorAsync(CancellationToken ct = default) =>
        GetListAsync<FrmSpaceElevator>("getSpaceElevator", ct);

    /// <summary>
    /// Per-item production/consumption for everything the factory touches. This
    /// is one of the bigger endpoints — fine on a daily digest, not something to
    /// poll on a loop. Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmProdStat>?> GetProdStatsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmProdStat>("getProdStats", ct);

    /// <summary>
    /// Every schematic in the game with its purchased state.
    ///
    /// <para><b>Expensive.</b> 1.1 MB / 575 entries on the clan's server, and
    /// FRM runs it on the GAME THREAD. Poll this in tens of minutes, never in
    /// seconds. <see cref="FrmSchematic"/> omits the recipe payload so parsing
    /// is cheap, but the transfer and the server-side serialization are not.</para>
    ///
    /// Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmSchematic>?> GetSchematicsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmSchematic>("getSchematics", ct);

    /// <summary>
    /// M.A.M. research trees and their nodes. ~72 KB / 97 nodes on the clan's
    /// server — far cheaper than getSchematics, though still game-thread.
    /// Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmResearchTree>?> GetResearchTreesAsync(CancellationToken ct = default) =>
        GetListAsync<FrmResearchTree>("getResearchTrees", ct);

    /// <summary>AWESOME Sink coupon/points state. Null = unreachable.</summary>
    public Task<IReadOnlyList<FrmResourceSink>?> GetResourceSinkAsync(CancellationToken ct = default) =>
        GetListAsync<FrmResourceSink>("getResourceSink", ct);

    /// <summary>
    /// Every mod loaded on the SERVER, plus pseudo-entries for the base game and
    /// SML. Null = unreachable.
    /// </summary>
    public Task<IReadOnlyList<FrmMod>?> GetModListAsync(CancellationToken ct = default) =>
        GetListAsync<FrmMod>("getModList", ct);

    // ─── Transport ───────────────────────────────────────────────────────────

    /// <summary>Endpoints that return a bare JSON object rather than an array.</summary>
    private async Task<T?> GetObjectAsync<T>(string endpoint, CancellationToken ct) where T : class
    {
        var body = await GetRawAsync(endpoint, ct);
        if (body is null) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FRM {Endpoint} returned unparseable JSON: {Body}",
                endpoint, Truncate(body, 300));
            return null;
        }
    }

    /// <summary>
    /// Endpoints that return a JSON array. Tolerates a single object too, since
    /// a couple of FRM endpoints collapse to one when there's exactly one result.
    /// </summary>
    private async Task<IReadOnlyList<T>?> GetListAsync<T>(string endpoint, CancellationToken ct)
    {
        var body = await GetRawAsync(endpoint, ct);
        if (body is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<T>>(body, JsonOpts) ?? new List<T>();

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var single = JsonSerializer.Deserialize<T>(body, JsonOpts);
                return single is null ? new List<T>() : new List<T> { single };
            }

            _logger.LogWarning("FRM {Endpoint} returned unexpected JSON kind {Kind}",
                endpoint, doc.RootElement.ValueKind);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FRM {Endpoint} returned unparseable JSON: {Body}",
                endpoint, Truncate(body, 300));
            return null;
        }
    }

    /// <summary>
    /// The single request path. Returns the raw body, or null on ANY failure.
    /// Never throws except on caller cancellation.
    /// </summary>
    private async Task<string?> GetRawAsync(string endpoint, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("FRM {Endpoint} skipped — not enabled or no base URL", endpoint);
            return null;
        }

        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            // Reads don't need this, but sending it is harmless and means write
            // endpoints work the moment they're added.
            if (!string.IsNullOrWhiteSpace(_config.FrmAuthToken))
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-FRM-Authorization", _config.FrmAuthToken);

            using var resp = await http.GetAsync(Url(endpoint), ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 404 here means the endpoint name is wrong (FRM is answering),
                // which is a code bug rather than an outage — log it louder.
                var level = resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? LogLevel.Warning
                    : LogLevel.Debug;
                _logger.Log(level, "FRM {Endpoint} returned {Status}", endpoint, (int)resp.StatusCode);
                return null;
            }

            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline / timeout / DNS. Debug, not Error: a game server being down
            // is routine and the poller runs every minute.
            _logger.LogDebug(ex, "FRM {Endpoint} failed (server offline, or its web server didn't bind its port?)", endpoint);
            return null;
        }
    }

    // ─── Writes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Posts a message into the in-game chat, so people mid-session see it
    /// without alt-tabbing to Discord.
    ///
    /// <para><b>Sender.</b> FRM renders <c>"ada"</c> as an A.D.A. message — the
    /// game's own announcement voice — rather than as a player talking. That is
    /// deliberately what we send: it reads as part of the game, which is much
    /// harder to scroll past than another line of chat. Empty means System, and
    /// anything else appears as a name (max 32 characters, enforced by FRM).</para>
    ///
    /// <para><b>Requires <see cref="BotConfig.FrmAuthToken"/>.</b> Unlike every
    /// read on this client, a write without the token is rejected by FRM — so
    /// this returns false immediately rather than making a request that cannot
    /// succeed.</para>
    ///
    /// <para>Returns false on ANY failure and never throws, matching the reads.
    /// Callers treat a false as "nobody in game heard it", never as an error
    /// worth failing the surrounding operation over.</para>
    /// </summary>
    public async Task<bool> SendChatMessageAsync(
        string message, string sender = "ada", CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("FRM sendChatMessage skipped — not enabled or no base URL");
            return false;
        }

        if (string.IsNullOrWhiteSpace(_config.FrmAuthToken))
        {
            // WARNING, not Debug, and exactly once.
            //
            // Serilog's floor is Information, so a Debug line here would be
            // discarded — and this is the single failure mode that is
            // indistinguishable from the feature not existing: everything else
            // about FRM keeps working, because every other call is an
            // unauthenticated read. Silence would send someone hunting through
            // the event scheduler for a problem that is one missing env var.
            if (!_warnedMissingToken)
            {
                _warnedMissingToken = true;
                _logger.LogWarning(
                    "FRM writes are disabled: FrmAuthToken is not set, so in-game chat messages will not be sent. " +
                    "The token is generated by the mod at load and stored in the server's GameUserSettings.ini. " +
                    "Reads are unaffected.");
            }

            return false;
        }

        var text = Sanitize(message);
        if (text.Length == 0) return false;

        // FRM caps the sender at 32 characters; longer is rejected outright
        // rather than truncated, so clamp it here.
        var from = sender is null ? "" : Truncate(sender, 32);

        var body = JsonSerializer.Serialize(new { message = text, sender = from });

        var response = await PostRawAsync("sendChatMessage", body, ct);
        if (response is null) return false;

        // The response is an ARRAY of results, like FRM's read endpoints —
        // [{ "IsSent": true, "Message": "..." }] — not a bare object.
        try
        {
            using var doc = JsonDocument.Parse(response);

            // An EMPTY array means FRM rejected it before sending — its handler
            // early-returns with [] rather than a result object. Reporting that
            // as success made the caller log "Announced in-game" for a message
            // nobody saw.
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() == 0)
            {
                _logger.LogDebug("FRM sendChatMessage returned an empty result — the message was not sent");
                return false;
            }

            var element = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement[0]
                : doc.RootElement;

            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("IsSent", out var sent)
                && sent.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (!sent.GetBoolean())
                    _logger.LogDebug("FRM accepted the request but reported the chat message was not sent");

                return sent.GetBoolean();
            }

            // Shape we don't recognise. FRM answered 2xx, so treat it as sent
            // rather than retrying into a duplicate.
            _logger.LogDebug("FRM sendChatMessage returned an unrecognised body: {Body}", Truncate(response, 200));
            return true;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FRM sendChatMessage returned unparseable JSON: {Body}", Truncate(response, 300));
            return false;
        }
    }

    /// <summary>
    /// POST counterpart to <see cref="GetRawAsync"/>. Same failure contract:
    /// null on anything going wrong, never throws except on cancellation.
    /// </summary>
    private async Task<string?> PostRawAsync(string endpoint, string json, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = RequestTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");
            // Guarded like GetRawAsync. The only caller today pre-checks the
            // token, but a future write endpoint on this helper shouldn't send
            // an empty header and get an opaque 401.
            if (!string.IsNullOrWhiteSpace(_config.FrmAuthToken))
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-FRM-Authorization", _config.FrmAuthToken);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(Url(endpoint), content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 401/403 here means the token is wrong or missing — worth
                // saying loudly, because it will never fix itself.
                var level = resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                             or System.Net.HttpStatusCode.Forbidden
                                             or System.Net.HttpStatusCode.NotFound
                    ? LogLevel.Warning
                    : LogLevel.Debug;

                _logger.Log(level, "FRM {Endpoint} (POST) returned {Status}", endpoint, (int)resp.StatusCode);
                return null;
            }

            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FRM {Endpoint} (POST) failed", endpoint);
            return null;
        }
    }

    /// <summary>
    /// In-game chat is plain text on one line. Newlines would either be dropped
    /// or split the message, and there is no markup — so Discord formatting has
    /// to be flattened by the caller, not escaped.
    /// </summary>
    private static string Sanitize(string message) =>
        Truncate((message ?? "").Replace("\r", " ").Replace("\n", " ").Trim(), 200);

    private string Url(string endpoint) => $"{_config.FrmBaseUrl.TrimEnd('/')}/{endpoint}";

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
