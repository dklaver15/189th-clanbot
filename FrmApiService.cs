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
    FrmPowerInfo? PowerInfo,

    // Added for the factory map. Appended rather than slotted in beside the
    // other identity fields so the positional order the 2026-07-25 capture was
    // modelled against stays intact and reviewable. Both are nullable: if the
    // live response turns out not to carry them, generators quietly stop
    // appearing on the map and every other consumer is unaffected.
    FrmLocation? Location = null,
    FrmBoundingBox? BoundingBox = null);

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

// ─── Rail ───────────────────────────────────────────────────────────────────
//
// getTrains and getTrainStation were captured from the clan's live server on
// 2026-08-09 and match the docs field-for-field, which is a first for this API.
// What the docs did NOT say, and what the capture cost three bugs to find:
//
//   • The code fields are ENUM-PREFIXED. A perfectly healthy train reports
//     SelfDriving "SDLE_NoError", Docking "TDS_None", Path "PDE_NoError".
//     A naive "is this string empty" test reads that as three faults.
//   • TrainStation carries the sentinel "No Station", not an empty string,
//     for a train with nowhere to be.
//   • An empty cargo platform reports "Inventory": [] rather than a zeroed
//     stack, so there is no capacity to divide by and "starved" is
//     indistinguishable from "unknown" without knowing the platform type.
//     LoadingMode is the discriminator, and a station lists its DECORATIVE
//     platforms too ("LoadingMode": "Empty Platform").
//
// All three are handled in SatisfactoryRail, not here: these records stay a
// faithful transcription of the wire format.
//
// FrmRailSegment is still UNVERIFIED, modelled from docs.ficsit.app alone.
// getTrainRails is the one rail endpoint nobody has captured, so treat
// SplineData, Location0/Location1 and Length as hypotheses. Everything is
// nullable or defaulted, so a wrong guess costs a blank map rather than an
// exception.

/// <summary>One stack in a freight car or a station platform.</summary>
public sealed record FrmInventoryItem(
    string? Name,
    string? ClassName,
    double Amount,
    double MaxAmount);

/// <summary>
/// One vehicle in a consist: a locomotive or a freight car. FRM reports the
/// train as a whole AND its individual cars, and the per-car payload is the
/// only way to tell a loaded train from an empty one running back.
/// </summary>
public sealed record FrmTrainCar(
    string? Name,
    string? ClassName,
    double TotalMass,
    double PayloadMass,
    double MaxPayloadMass,
    IReadOnlyList<FrmInventoryItem>? Inventory);

/// <summary>One stop on a train's timetable. FRM returns only the name.</summary>
public sealed record FrmTimeTableStop(string? StationName);

/// <summary>
/// One train.
///
/// <para><b><see cref="SelfDriving"/>, <see cref="Docking"/> and
/// <see cref="Path"/> are ENUM-PREFIXED code strings</b>, and a healthy train
/// still fills all three: "SDLE_NoError", "TDS_None", "PDE_NoError" on the live
/// server. <see cref="Status"/> is friendlier prose ("Parked"). Nothing here
/// switches on a specific literal; <c>SatisfactoryRail.Code</c> owns deciding
/// which of these means "nothing to report".</para>
///
/// <para><b><see cref="TrainStation"/> is "current OR next stop", and reads
/// "No Station" when there is neither.</b> It does not distinguish "sitting at
/// Iron Loading" from "on its way to Iron Loading", so never render it as a
/// location on its own. Pair it with <see cref="ForwardSpeed"/>, which does
/// answer that.</para>
///
/// <para><b>Mass is in the game's own units</b> and FRM does not say which.
/// Observed magnitudes: an Electric Locomotive is 300000 and an empty Freight
/// Car is 30000 with a 70000 max payload, so it is very likely kilograms, but
/// "very likely" is not good enough to print a suffix nobody has checked
/// against the in-game HUD. Ratios (payload against max payload) are safe
/// because the unit cancels, and that is why every display here is a
/// percentage.</para>
///
/// <para>Note <see cref="MaxPayloadMass"/> is 0 on locomotives, so a train's
/// capacity is the sum over its freight cars and an all-engine consist is
/// legitimately 0. Guard the division.</para>
/// </summary>
public sealed record FrmTrain(
    string? Id,
    string? Name,
    string? ClassName,
    FrmLocation? Location,
    double TotalMass,
    double PayloadMass,
    double MaxPayloadMass,
    double ForwardSpeed,
    double ThrottlePercent,
    string? TrainStation,
    bool Derailed,
    bool PendingDerail,
    string? Status,
    IReadOnlyList<FrmTimeTableStop>? TimeTable,
    int TimeTableIndex,
    string? SelfDriving,
    string? Docking,
    string? Path,
    IReadOnlyList<FrmTrainCar>? Vehicles,
    FrmPowerInfo? PowerInfo);

/// <summary>
/// One docking platform on a station, with whatever it is currently holding.
///
/// <para><see cref="LoadingMode"/> is what makes a buffer reading meaningful:
/// a full buffer on a LOADING platform is a train that hasn't come to collect,
/// and a full buffer on an UNLOADING platform is a factory that isn't consuming.
/// Same number, opposite problem, so the station board must not flag one
/// without knowing which it is.</para>
/// </summary>
public sealed record FrmStationPlatform(
    string? Name,
    string? ClassName,
    string? LoadingMode,
    string? LoadingStatus,
    string? DockingStatus,
    IReadOnlyList<FrmInventoryItem>? Inventory);

/// <summary>
/// One train station and its platforms.
///
/// <para>The station's own <c>Inventory</c> is not modelled: the items live on
/// the platforms in <see cref="CargoInventory"/>, and the docs show the root
/// level repeating the platform shape rather than aggregating it.</para>
/// </summary>
public sealed record FrmTrainStation(
    string? Id,
    string? Name,
    string? ClassName,
    FrmLocation? Location,
    double TransferRate,
    double InflowRate,
    double OutflowRate,
    IReadOnlyList<FrmStationPlatform>? CargoInventory,
    FrmPowerInfo? PowerInfo);

/// <summary>
/// One piece of track.
///
/// <para><b><see cref="SplineData"/> is the drawable part, and it is confirmed
/// present and dense</b> (2026-08-09 capture: points under a metre apart along
/// a curve, each a bare <c>{x,y,z}</c> with no rotation key, which is why
/// <see cref="FrmLocation"/>'s Rotation defaults to 0 here). A rail is a curve,
/// so its <see cref="Location"/> is only its origin: drawing from Location
/// alone produces a scatter of dots.</para>
///
/// <para><b><see cref="Length"/>, <see cref="Location0"/>,
/// <see cref="Location1"/> and the Connected flags were NOT in the captured
/// response</b>, which listed only ID, Name, ClassName, location, BoundingBox,
/// ColorSlot and SplineData before the capture was truncated. They may exist
/// further down the object or may be doc fiction. Nothing depends on them:
/// track length is measured off the spline by
/// <c>SatisfactoryRail.LengthUnits</c>, and the endpoints are only a fallback
/// for a segment with no spline. They stay modelled so a real value is picked
/// up for free if it is there.</para>
///
/// <para><see cref="BoundingBox"/> is not modelled. In the capture its min and
/// max were identical (both equal to the segment origin), so it says nothing
/// the spline does not.</para>
/// </summary>
public sealed record FrmRailSegment(
    string? Id,
    string? Name,
    string? ClassName,
    FrmLocation? Location,
    [property: System.Text.Json.Serialization.JsonPropertyName("location0")]
    FrmLocation? Location0,
    [property: System.Text.Json.Serialization.JsonPropertyName("location1")]
    FrmLocation? Location1,
    bool Connected0,
    bool Connected1,
    double Length,
    IReadOnlyList<FrmLocation>? SplineData);

// ─── Factory geometry ───────────────────────────────────────────────────────
//
// The types the factory MAP needs. All modelled from docs.ficsit.app and
// UNVERIFIED against the clan's server, same caveat as FrmRailSegment.
//
// Every one of these deliberately omits the heavy nested arrays the endpoint
// actually returns: getFactory carries a full production list, ingredient list
// and both inventories per building, and getBelts/getPipes carry per-item
// contents. System.Text.Json SKIPS tokens with no matching member, so leaving
// them out means a multi-megabyte response is walked once and never
// materialized. That is not a style choice on a 1 vCPU / 2 GB droplet where RAM
// is the binding constraint: it is the difference between a map and an OOM.
// Same trick FrmSchematic uses to survive getSchematics at 1.1 MB.

/// <summary>An axis-aligned world box. Min and max can be equal.</summary>
public sealed record FrmBoundingBox(FrmLocation? Min, FrmLocation? Max);

/// <summary>
/// One production building from getFactory.
///
/// <para><see cref="IsConfigured"/> is the quietly useful one: a machine with no
/// recipe set is not "idle", it is unfinished, and on a map those want
/// different colours. <see cref="IsProducing"/> false with a recipe set is the
/// real fault state.</para>
/// </summary>
public sealed record FrmBuilding(
    string? Id,
    string? Name,
    string? ClassName,
    FrmLocation? Location,
    FrmBoundingBox? BoundingBox,
    string? Recipe,
    bool IsConfigured,
    bool IsProducing,
    bool IsPaused,
    double ProdPercent,
    double ManuSpeed,
    FrmPowerInfo? PowerInfo);

/// <summary>
/// One conveyor from getBelts, or one pipe from getPipes (identical shape, so
/// one record serves both).
///
/// <para><see cref="ItemsPerMinute"/> is the belt's rated SPEED, not its
/// observed throughput. Colouring a map by it shows which tier of belt was
/// used, not where the bottleneck is, and the two are easy to confuse.</para>
/// </summary>
public sealed record FrmConveyor(
    string? Id,
    string? Name,
    string? ClassName,
    [property: System.Text.Json.Serialization.JsonPropertyName("location0")]
    FrmLocation? Location0,
    [property: System.Text.Json.Serialization.JsonPropertyName("location1")]
    FrmLocation? Location1,
    bool Connected0,
    bool Connected1,
    double Length,
    double ItemsPerMinute,
    IReadOnlyList<FrmLocation>? SplineData);

/// <summary>One miner, pump or well extractor.</summary>
public sealed record FrmExtractor(
    string? Id,
    string? Name,
    string? ClassName,
    FrmLocation? Location,
    string? Recipe,
    bool IsConfigured,
    bool IsProducing,
    bool IsPaused,
    double ProdPercent,
    double CurrentProd,
    double MaxProd,
    FrmPowerInfo? PowerInfo);

/// <summary>
/// One resource node.
///
/// <para><see cref="Exploited"/> means something is built on it. That single
/// bool is what turns a map layer into a to-do list: unexploited pure nodes are
/// exactly what somebody planning the next build wants to find.</para>
///
/// <para>Static for the life of a save, so it is the one layer that can be
/// cached indefinitely rather than on a timer.</para>
/// </summary>
public sealed record FrmResourceNode(
    string? Id,
    string? Name,
    string? ClassName,
    string? ResourceForm,
    string? Purity,
    string? NodeType,
    bool Exploited,
    FrmLocation? Location);

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
    /// <summary>
    /// Why a read came back with nothing.
    ///
    /// <para>The plain reads collapse this to null and that is fine for a
    /// poller, which only ever asks "do I have numbers to act on". It is NOT
    /// fine for a command somebody just typed: "the server isn't answering" and
    /// "the response was too big to read" want completely different sentences
    /// back, and guessing between them in the message is how
    /// <c>/satisfactory-map</c> ended up telling people to go read the bot log.
    /// The <c>*WithStatusAsync</c> reads keep the reason so a user-facing
    /// caller can say which one happened.</para>
    /// </summary>
    public enum FrmReadStatus
    {
        /// <summary>The server answered and the body parsed.</summary>
        Ok,

        /// <summary>
        /// Offline, timed out, refused, DNS, or a non-success status. Includes
        /// "FRM isn't configured here", which is unreachable from the caller's
        /// point of view.
        /// </summary>
        Unreachable,

        /// <summary>
        /// The server answered and the body was over
        /// <see cref="BotConfig.FrmMaxResponseMegabytes"/>, so it was refused or
        /// abandoned mid-download. The server is HEALTHY; the factory outgrew
        /// the cap. Never report this as an outage.
        /// </summary>
        TooLarge,

        /// <summary>Answered, but the body was not JSON we could read. A code bug or a mod version change.</summary>
        Unparseable,
    }

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Deadline for the map endpoints. They can be megabytes off a game server,
    /// which the 15-second default would abort partway through and report as
    /// "unreachable", sending somebody to check a server that was fine.
    /// </summary>
    private static readonly TimeSpan HeavyRequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Sentinel meaning "apply the configured heavy-response cap", so call sites
    /// don't each have to resolve config to ask for the guard.
    /// </summary>
    private const long MaxHeavyResponseBytes = -1;

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

    /// <summary>
    /// The heavy-response cap actually enforced, in MB.
    ///
    /// <para>Public because the size failure is now reported to a USER, and the
    /// number in that message has to be the number that bit. Reading
    /// <see cref="BotConfig.FrmMaxResponseMegabytes"/> raw would tell an admin
    /// who set it to 0 that the limit is 0 MB, and tell one who set it to 500
    /// that it is 500 MB, when in both cases the clamp below is what decided.
    /// One property so the guard, the log and the message can never disagree.</para>
    /// </summary>
    public int EffectiveMaxResponseMegabytes =>
        Math.Clamp(_config.FrmMaxResponseMegabytes, 1, 256);

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

    // ─── Rail ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every train, with position, speed, consist and derail state.
    ///
    /// <para>Small and cheap: one object per train, and a clan-sized network has
    /// a handful. This is the only rail endpoint suitable for a poll loop.</para>
    ///
    /// <para>Null = unreachable. An EMPTY list means no trains exist, which is
    /// the normal state before anyone unlocks Railway, and callers must not
    /// treat it as a fault.</para>
    /// </summary>
    public Task<IReadOnlyList<FrmTrain>?> GetTrainsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmTrain>("getTrains", ct);

    /// <summary>
    /// Every train station with its platforms and their buffers.
    ///
    /// <para>Middleweight: it carries a full inventory per platform, so it grows
    /// with the network rather than staying flat. Poll it in minutes, not
    /// seconds. Null = unreachable.</para>
    /// </summary>
    public Task<IReadOnlyList<FrmTrainStation>?> GetTrainStationsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmTrainStation>("getTrainStation", ct);

    /// <summary>
    /// Every piece of track, with spline geometry.
    ///
    /// <para><b>The heaviest rail endpoint by a wide margin.</b> It returns one
    /// object per rail segment with a point list on each, so a mature network is
    /// thousands of entries and megabytes of JSON. Track geometry also changes
    /// only when somebody builds, which makes it the one rail read that should
    /// be cached hard and never polled. <see cref="SatisfactoryRailMapRenderer"/>
    /// is the only caller and holds it for
    /// <see cref="BotConfig.SatisfactoryRailMapCacheMinutes"/>.</para>
    ///
    /// <para>Null = unreachable.</para>
    /// </summary>
    public Task<IReadOnlyList<FrmRailSegment>?> GetTrainRailsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmRailSegment>("getTrainRails", ct);

    // ─── Factory geometry ────────────────────────────────────────────────────
    //
    // Everything here feeds the factory MAP, and every one of them is heavy
    // enough to want the size guard (see MaxHeavyResponseBytes). None of them
    // belongs anywhere near a poll loop; SatisfactoryFactoryMapRenderer caches
    // the lot.

    /// <summary>
    /// Every production building in the save.
    ///
    /// <para><b>The heaviest endpoint FRM offers, by a wide margin.</b> It walks
    /// the whole factory and returns recipes, production lists and inventories
    /// per building. <see cref="FrmBuilding"/> reads only a dozen scalar fields
    /// off each one so the parse stays cheap, but the transfer does not.</para>
    ///
    /// <para>Null = unreachable, or the response blew the size guard.</para>
    /// </summary>
    public Task<IReadOnlyList<FrmBuilding>?> GetFactoryAsync(CancellationToken ct = default) =>
        GetListAsync<FrmBuilding>("getFactory", ct, MaxHeavyResponseBytes);

    /// <summary>Every conveyor belt, with spline geometry. Heavy. Null = unreachable.</summary>
    public Task<IReadOnlyList<FrmConveyor>?> GetBeltsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmConveyor>("getBelts", ct, MaxHeavyResponseBytes);

    /// <summary>Every pipe, same shape as belts. Heavy. Null = unreachable.</summary>
    public Task<IReadOnlyList<FrmConveyor>?> GetPipesAsync(CancellationToken ct = default) =>
        GetListAsync<FrmConveyor>("getPipes", ct, MaxHeavyResponseBytes);

    /// <summary>Miners, pumps and well extractors. Null = unreachable.</summary>
    public Task<IReadOnlyList<FrmExtractor>?> GetExtractorsAsync(CancellationToken ct = default) =>
        GetListAsync<FrmExtractor>("getExtractor", ct, MaxHeavyResponseBytes);

    /// <summary>
    /// Every resource node in the world, exploited or not.
    ///
    /// <para>Large but STATIC: nodes are world generation and never move, so
    /// this is the one layer that can be cached for the life of the process
    /// rather than on a timer. Null = unreachable.</para>
    /// </summary>
    public Task<IReadOnlyList<FrmResourceNode>?> GetResourceNodesAsync(CancellationToken ct = default) =>
        GetListAsync<FrmResourceNode>("getResourceNode", ct, MaxHeavyResponseBytes);

    // ─── Factory geometry, with the reason attached ──────────────────────────
    //
    // Same five endpoints, same cost, but they hand back WHY they failed. Only
    // the factory map uses these: it is the one caller that has to explain
    // itself to a human who is standing there waiting for a picture. Everything
    // else wants the plain null and is right to.
    //
    // These are one-liners on purpose. Naming the endpoint string in exactly one
    // place per endpoint is what keeps a typo a compile error's worth of work
    // rather than a 404 nobody sees (see the 404 branch in GetRawAsync).

    /// <inheritdoc cref="GetFactoryAsync"/>
    public Task<(IReadOnlyList<FrmBuilding>? Data, FrmReadStatus Status)> GetFactoryWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmBuilding>("getFactory", ct, MaxHeavyResponseBytes);

    /// <inheritdoc cref="GetBeltsAsync"/>
    public Task<(IReadOnlyList<FrmConveyor>? Data, FrmReadStatus Status)> GetBeltsWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmConveyor>("getBelts", ct, MaxHeavyResponseBytes);

    /// <inheritdoc cref="GetPipesAsync"/>
    public Task<(IReadOnlyList<FrmConveyor>? Data, FrmReadStatus Status)> GetPipesWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmConveyor>("getPipes", ct, MaxHeavyResponseBytes);

    /// <inheritdoc cref="GetExtractorsAsync"/>
    public Task<(IReadOnlyList<FrmExtractor>? Data, FrmReadStatus Status)> GetExtractorsWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmExtractor>("getExtractor", ct, MaxHeavyResponseBytes);

    /// <inheritdoc cref="GetResourceNodesAsync"/>
    public Task<(IReadOnlyList<FrmResourceNode>? Data, FrmReadStatus Status)> GetResourceNodesWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmResourceNode>("getResourceNode", ct, MaxHeavyResponseBytes);

    /// <summary>
    /// Train stations, with the reason attached. Not heavy, but the factory map
    /// fetches it on EVERY render whatever the layers and never caches it, which
    /// makes it the map's cheapest evidence that the server is up at all.
    /// </summary>
    public Task<(IReadOnlyList<FrmTrainStation>? Data, FrmReadStatus Status)> GetTrainStationsWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmTrainStation>("getTrainStation", ct);

    /// <inheritdoc cref="GetTrainsAsync"/>
    public Task<(IReadOnlyList<FrmTrain>? Data, FrmReadStatus Status)> GetTrainsWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmTrain>("getTrains", ct);

    /// <inheritdoc cref="GetGeneratorsAsync"/>
    public Task<(IReadOnlyList<FrmGenerator>? Data, FrmReadStatus Status)> GetGeneratorsWithStatusAsync(
        CancellationToken ct = default) =>
        GetListWithStatusAsync<FrmGenerator>("getGenerators", ct);

    // ─── Transport ───────────────────────────────────────────────────────────

    /// <summary>Endpoints that return a bare JSON object rather than an array.</summary>
    private async Task<T?> GetObjectAsync<T>(string endpoint, CancellationToken ct) where T : class
    {
        var (body, _) = await GetRawAsync(endpoint, ct);
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
    ///
    /// <para>The plain overload. Discards the reason, which is what every poller
    /// wants.</para>
    /// </summary>
    private async Task<IReadOnlyList<T>?> GetListAsync<T>(
        string endpoint, CancellationToken ct, long maxBytes = 0) =>
        (await GetListWithStatusAsync<T>(endpoint, ct, maxBytes)).Data;

    /// <summary>
    /// <see cref="GetListAsync{T}"/>, keeping the reason. Data is non-null if and
    /// only if the status is <see cref="FrmReadStatus.Ok"/>, so a caller can
    /// still just check for null and ignore the rest.
    /// </summary>
    private async Task<(IReadOnlyList<T>? Data, FrmReadStatus Status)> GetListWithStatusAsync<T>(
        string endpoint, CancellationToken ct, long maxBytes = 0)
    {
        var (body, status) = await GetRawAsync(endpoint, ct, maxBytes);
        if (body is null) return (null, status);

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return (JsonSerializer.Deserialize<List<T>>(body, JsonOpts) ?? new List<T>(), FrmReadStatus.Ok);

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var single = JsonSerializer.Deserialize<T>(body, JsonOpts);
                return (single is null ? new List<T>() : new List<T> { single }, FrmReadStatus.Ok);
            }

            _logger.LogWarning("FRM {Endpoint} returned unexpected JSON kind {Kind}",
                endpoint, doc.RootElement.ValueKind);
            return (null, FrmReadStatus.Unparseable);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "FRM {Endpoint} returned unparseable JSON: {Body}",
                endpoint, Truncate(body, 300));
            return (null, FrmReadStatus.Unparseable);
        }
    }

    /// <summary>
    /// The single request path. Returns the raw body plus WHY when there isn't
    /// one. Body is non-null if and only if the status is
    /// <see cref="FrmReadStatus.Ok"/>. Never throws except on caller
    /// cancellation.
    /// </summary>
    /// <param name="maxBytes">
    /// 0 for no cap (the small endpoints), or
    /// <see cref="MaxHeavyResponseBytes"/> to apply
    /// <see cref="BotConfig.FrmMaxResponseMegabytes"/>.
    ///
    /// <para><b>Why a cap exists at all.</b> getFactory, getBelts and
    /// getResourceNode grow with the save, without bound, and the bot runs on a
    /// 1 vCPU / 2 GB droplet where RAM is the binding constraint. An unbounded
    /// read means the day somebody's factory crosses a threshold, the bot stops
    /// being killed by a bug and starts being killed by success. Refusing to
    /// draw a map is a far better failure than the process dying and taking the
    /// AWOL sweep, the event scheduler and the alerts with it.</para>
    /// </param>
    private async Task<(string? Body, FrmReadStatus Status)> GetRawAsync(
        string endpoint, CancellationToken ct, long maxBytes = 0)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("FRM {Endpoint} skipped — not enabled or no base URL", endpoint);
            return (null, FrmReadStatus.Unreachable);
        }

        var heavy = maxBytes != 0;
        var cap = heavy
            ? EffectiveMaxResponseMegabytes * 1024L * 1024L
            : long.MaxValue;

        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = heavy ? HeavyRequestTimeout : RequestTimeout;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            // Reads don't need this, but sending it is harmless and means write
            // endpoints work the moment they're added.
            if (!string.IsNullOrWhiteSpace(_config.FrmAuthToken))
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-FRM-Authorization", _config.FrmAuthToken);

            // Headers first on the heavy path, so an oversized response can be
            // refused from Content-Length without pulling the body at all.
            using var resp = await http.GetAsync(
                Url(endpoint),
                heavy ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                ct);

            if (!resp.IsSuccessStatusCode)
            {
                // 404 here means the endpoint name is wrong (FRM is answering),
                // which is a code bug rather than an outage — log it louder.
                var level = resp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? LogLevel.Warning
                    : LogLevel.Debug;
                _logger.Log(level, "FRM {Endpoint} returned {Status}", endpoint, (int)resp.StatusCode);
                return (null, FrmReadStatus.Unreachable);
            }

            if (!heavy) return (await resp.Content.ReadAsStringAsync(ct), FrmReadStatus.Ok);

            if (resp.Content.Headers.ContentLength is { } declared && declared > cap)
            {
                _logger.LogWarning(
                    "FRM {Endpoint} declared {Megabytes:0.#} MB, over the {Cap} MB limit. Not read. "
                    + "Raise BotConfig.FrmMaxResponseMegabytes if the droplet can take it.",
                    endpoint, declared / 1024.0 / 1024.0, EffectiveMaxResponseMegabytes);
                return (null, FrmReadStatus.TooLarge);
            }

            return await ReadBoundedAsync(resp, endpoint, cap, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Offline / timeout / DNS. Debug, not Error: a game server being down
            // is routine and the poller runs every minute. The on-demand callers
            // now carry the reason back to the user instead of relying on this
            // line being visible, which at the production Information level it
            // never was.
            _logger.LogDebug(ex, "FRM {Endpoint} failed (server offline, or its web server didn't bind its port?)", endpoint);
            return (null, FrmReadStatus.Unreachable);
        }
    }

    /// <summary>
    /// Streams a response, abandoning it the moment it crosses the cap.
    ///
    /// <para>Needed because Content-Length is only advisory: FRM may answer with
    /// chunked encoding and no length at all, and an honest declaration is
    /// exactly what a runaway response would not have. Checking as it arrives is
    /// the only check that cannot be lied to.</para>
    ///
    /// <para>Peak memory is roughly twice the body (the buffer, then the
    /// string), which the cap is set with in mind: see
    /// <see cref="BotConfig.FrmMaxResponseMegabytes"/>.</para>
    /// </summary>
    private async Task<(string? Body, FrmReadStatus Status)> ReadBoundedAsync(
        HttpResponseMessage resp, string endpoint, long cap, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);

        using var buffer = new MemoryStream(capacity: 1 << 20);
        var chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;

            total += read;

            if (total > cap)
            {
                _logger.LogWarning(
                    "FRM {Endpoint} exceeded the {Cap} MB limit while downloading and was abandoned. "
                    + "Raise BotConfig.FrmMaxResponseMegabytes if the droplet can take it.",
                    endpoint, EffectiveMaxResponseMegabytes);
                return (null, FrmReadStatus.TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        return (Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), FrmReadStatus.Ok);
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
