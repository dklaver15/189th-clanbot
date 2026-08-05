using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Client for the Sleeper fantasy API (api.sleeper.app), which powers the
/// <c>/sleeper-*</c> commands and the weekly matchup surfaces.
///
/// ── No credentials, by design ──
/// Sleeper's read API takes no key, no token and no OAuth: every endpoint below is
/// public and anonymous. The only thing that has to be configured is
/// <see cref="BotConfig.SleeperLeagueId"/>, so the feature's readiness check is
/// "is a league id set", never "is a key valid". The flip side is that nothing here
/// can ever write to Sleeper: no roster moves, no lineup changes, no trades.
///
/// ── Rate limit ──
/// Sleeper asks callers to stay under 1000 requests per minute or risk an IP block,
/// and asks specifically that the player directory be pulled at most once a day.
/// Both are handled by caching rather than by trusting callers to behave: every
/// read below is served from a cache slot with its own TTL, and the player
/// directory's TTL is a day.
///
/// ── Cache shape ──
/// Each read is served from a cache slot with its own TTL. Within the TTL the
/// cached value is returned directly; once expired, the caller WAITS for a fresh
/// fetch rather than being handed the previous one. That last part is deliberate
/// and was a correction: these surfaces are on-demand commands run a couple of
/// times a day, so a stale-while-revalidate slot is always expired when it is read
/// and would serve the previous invocation's snapshot every single time. See
/// <see cref="CacheSlot{T}"/> for the incident that proved it.
///
/// A failed refresh keeps the previous value and retries sooner than a full TTL,
/// which is what stops one flaky minute from blanking the standings.
/// </summary>
public sealed class SleeperApiService
{
    private const string ApiBase = "https://api.sleeper.app/v1";

    // TTLs, chosen against how fast each thing can actually change:
    //   league settings change once a season (a commissioner edit);
    //   rosters/users change on a waiver or a name edit, so minutes;
    //   matchup points move play by play while games are live;
    //   the NFL clock rolls over once a week;
    //   the player directory is the one Sleeper explicitly asks be daily.
    private static readonly TimeSpan LeagueTtl  = TimeSpan.FromHours(6);
    private static readonly TimeSpan TeamsTtl   = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MatchupTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StateTtl   = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PlayersTtl = TimeSpan.FromHours(24);

    /// <summary>Retry window after a failed or empty fetch, instead of serving nothing for a full TTL.</summary>
    private static readonly TimeSpan FailureRetryTtl = TimeSpan.FromMinutes(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SleeperApiService> _logger;
    private readonly BotConfig _config;

    private readonly CacheSlot<SleeperLeague> _league = new();
    private readonly CacheSlot<IReadOnlyList<SleeperTeam>> _teams = new();
    private readonly CacheSlot<SleeperNflState> _state = new();
    private readonly CacheSlot<IReadOnlyDictionary<string, SleeperPlayer>> _players = new();

    // Matchups are per week, so they get one slot each rather than one shared slot.
    private readonly object _matchupLock = new();
    private readonly Dictionary<int, CacheSlot<IReadOnlyList<SleeperMatchupSide>>> _matchups = new();

    public SleeperApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<SleeperApiService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// True when a league id is configured. There is no key to validate, so this is
    /// the whole readiness check.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.SleeperLeagueId);

    public string LeagueId => (_config.SleeperLeagueId ?? string.Empty).Trim();

    // ─── League ──────────────────────────────────────────────────────────────

    /// <summary>League settings. Null when unconfigured or when the first fetch has not landed.</summary>
    public async Task<SleeperLeague?> GetLeagueAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        return await _league.GetAsync(LeagueTtl, FailureRetryTtl, () => FetchLeagueAsync(ct));
    }

    private async Task<SleeperLeague?> FetchLeagueAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{ApiBase}/league/{Uri.EscapeDataString(LeagueId)}", ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var settings = Prop(root, "settings");
        var scoring = Prop(root, "scoring_settings");

        return new SleeperLeague(
            LeagueId: Str(root, "league_id") ?? LeagueId,
            Name: Str(root, "name") ?? "Fantasy league",
            Season: Str(root, "season") ?? string.Empty,
            SeasonType: Str(root, "season_type") ?? "regular",
            Status: Str(root, "status") ?? "unknown",
            TotalRosters: Int(root, "total_rosters") ?? 0,
            PlayoffWeekStart: Int(settings, "playoff_week_start") ?? 0,
            PlayoffTeams: Int(settings, "playoff_teams") ?? 0,
            PointsPerReception: Dbl(scoring, "rec") ?? 0,
            DraftId: Str(root, "draft_id"),
            Avatar: Str(root, "avatar"));
    }

    // ─── Teams (rosters joined to users) ─────────────────────────────────────

    /// <summary>
    /// Every team in the league: the roster's record and points joined to its
    /// owner. Ordered by roster id, NOT by standing. Use
    /// <see cref="SleeperFormat.SortStandings"/> for standings order, so one
    /// tiebreak rule serves every surface.
    ///
    /// Rosters with no owner (unclaimed slots in a league that is still filling)
    /// come back with a null Owner rather than being dropped, because the league
    /// has sixteen slots and a partly-filled league still has to render.
    /// </summary>
    public async Task<IReadOnlyList<SleeperTeam>> GetTeamsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return Array.Empty<SleeperTeam>();
        return await _teams.GetAsync(TeamsTtl, FailureRetryTtl, () => FetchTeamsAsync(ct))
               ?? Array.Empty<SleeperTeam>();
    }

    private async Task<IReadOnlyList<SleeperTeam>?> FetchTeamsAsync(CancellationToken ct)
    {
        var id = Uri.EscapeDataString(LeagueId);

        // Two calls, because Sleeper splits the record (rosters) from the person
        // (users) and neither endpoint carries the other's fields.
        using var rostersDoc = await GetJsonAsync($"{ApiBase}/league/{id}/rosters", ct);
        if (rostersDoc is null || rostersDoc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var users = await FetchUsersAsync(id, ct) ?? new Dictionary<string, SleeperLeagueUser>();

        var teams = new List<SleeperTeam>();
        foreach (var r in rostersDoc.RootElement.EnumerateArray())
        {
            var rosterId = Int(r, "roster_id");
            if (rosterId is null) continue;

            var ownerId = Str(r, "owner_id");
            var settings = Prop(r, "settings");

            users.TryGetValue(ownerId ?? string.Empty, out var owner);

            teams.Add(new SleeperTeam(
                RosterId: rosterId.Value,
                OwnerId: ownerId,
                Owner: owner,
                Wins: Int(settings, "wins") ?? 0,
                Losses: Int(settings, "losses") ?? 0,
                Ties: Int(settings, "ties") ?? 0,
                PointsFor: SplitPoints(settings, "fpts", "fpts_decimal"),
                PointsAgainst: SplitPoints(settings, "fpts_against", "fpts_against_decimal"),
                WaiverPosition: Int(settings, "waiver_position") ?? 0,
                Starters: StringList(r, "starters"),
                Players: StringList(r, "players")));
        }

        return teams.OrderBy(t => t.RosterId).ToList();
    }

    private async Task<Dictionary<string, SleeperLeagueUser>?> FetchUsersAsync(string escapedLeagueId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{ApiBase}/league/{escapedLeagueId}/users", ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var users = new Dictionary<string, SleeperLeagueUser>(StringComparer.Ordinal);
        foreach (var u in doc.RootElement.EnumerateArray())
        {
            var userId = Str(u, "user_id");
            if (string.IsNullOrWhiteSpace(userId)) continue;

            // metadata is a free-form bag: present, null, or an object, and
            // team_name is only in there once someone has actually set one.
            var metadata = Prop(u, "metadata");
            users[userId] = new SleeperLeagueUser(
                UserId: userId,
                DisplayName: Str(u, "display_name") ?? userId,
                TeamName: Str(metadata, "team_name"),
                Avatar: Str(u, "avatar"));
        }

        return users;
    }

    /// <summary>
    /// Resolves a Sleeper username to its account, used by the link command.
    /// Hits the network directly rather than a cache slot: it is a one-off lookup
    /// of an arbitrary name, so there is nothing worth keeping.
    /// </summary>
    public async Task<SleeperLeagueUser?> LookupUserAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        using var doc = await GetJsonAsync($"{ApiBase}/user/{Uri.EscapeDataString(username.Trim())}", ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var userId = Str(doc.RootElement, "user_id");
        if (string.IsNullOrWhiteSpace(userId)) return null;

        return new SleeperLeagueUser(
            UserId: userId,
            DisplayName: Str(doc.RootElement, "display_name") ?? username.Trim(),
            TeamName: null,
            Avatar: Str(doc.RootElement, "avatar"));
    }

    // ─── Matchups ────────────────────────────────────────────────────────────

    /// <summary>
    /// One week's matchup rows, one per roster. Sleeper returns the two sides of a
    /// game as separate entries sharing a matchup_id; pairing them into games is
    /// <see cref="SleeperFormat.PairGames"/>'s job.
    /// </summary>
    public async Task<IReadOnlyList<SleeperMatchupSide>> GetMatchupsAsync(int week, CancellationToken ct = default)
    {
        if (!IsConfigured || week <= 0) return Array.Empty<SleeperMatchupSide>();

        CacheSlot<IReadOnlyList<SleeperMatchupSide>> slot;
        lock (_matchupLock)
        {
            if (!_matchups.TryGetValue(week, out var found))
            {
                found = new CacheSlot<IReadOnlyList<SleeperMatchupSide>>();
                _matchups[week] = found;
            }
            slot = found;
        }

        return await slot.GetAsync(MatchupTtl, FailureRetryTtl, () => FetchMatchupsAsync(week, ct))
               ?? Array.Empty<SleeperMatchupSide>();
    }

    private async Task<IReadOnlyList<SleeperMatchupSide>?> FetchMatchupsAsync(int week, CancellationToken ct)
    {
        var url = $"{ApiBase}/league/{Uri.EscapeDataString(LeagueId)}/matchups/{week}";
        using var doc = await GetJsonAsync(url, ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var sides = new List<SleeperMatchupSide>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var rosterId = Int(m, "roster_id");
            if (rosterId is null) continue;

            var playerPoints = new Dictionary<string, double>(StringComparer.Ordinal);
            var pp = Prop(m, "players_points");
            if (pp is { ValueKind: JsonValueKind.Object })
            {
                foreach (var entry in pp.Value.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetDouble(out var v))
                        playerPoints[entry.Name] = v;
                }
            }

            sides.Add(new SleeperMatchupSide(
                RosterId: rosterId.Value,
                MatchupId: Int(m, "matchup_id"),
                Points: Dbl(m, "points") ?? 0,
                Starters: StringList(m, "starters"),
                PlayerPoints: playerPoints));
        }

        return sides;
    }

    // ─── NFL clock ───────────────────────────────────────────────────────────

    /// <summary>
    /// The NFL's current season and week. This is the single source of truth for
    /// "what week is it": the bot never derives a week from the calendar, because
    /// the schedule shifts and a derived week would drift silently.
    /// </summary>
    public async Task<SleeperNflState?> GetNflStateAsync(CancellationToken ct = default) =>
        await _state.GetAsync(StateTtl, FailureRetryTtl, () => FetchNflStateAsync(ct));

    private async Task<SleeperNflState?> FetchNflStateAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{ApiBase}/state/nfl", ct);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var root = doc.RootElement;
        DateTime? start = null;
        if (DateTime.TryParse(Str(root, "season_start_date"), out var parsed))
            start = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        return new SleeperNflState(
            Week: Int(root, "week") ?? 0,
            DisplayWeek: Int(root, "display_week") ?? 0,
            Leg: Int(root, "leg") ?? 0,
            Season: Str(root, "season") ?? string.Empty,
            SeasonType: Str(root, "season_type") ?? "regular",
            SeasonStartDate: start);
    }

    // ─── Player directory ────────────────────────────────────────────────────

    /// <summary>
    /// Player id to name/position/team, so a score can say who put it up.
    ///
    /// Gated by <see cref="BotConfig.SleeperPlayerCacheEnabled"/> and refreshed at
    /// most daily, which is what Sleeper asks for. Returns an empty map when the
    /// cache is off or the first fetch has not landed; every caller treats a miss
    /// as "show the raw player id's slot instead", never as an error.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SleeperPlayer>> GetPlayersAsync(CancellationToken ct = default)
    {
        if (!_config.SleeperPlayerCacheEnabled)
            return new Dictionary<string, SleeperPlayer>();

        return await _players.GetAsync(PlayersTtl, TimeSpan.FromMinutes(30), () => FetchPlayersAsync(ct))
               ?? new Dictionary<string, SleeperPlayer>();
    }

    /// <summary>
    /// Pulls the ~5 MB player directory and keeps four fields per player.
    ///
    /// The document is parsed straight off the response stream and disposed as soon
    /// as the trimmed map is built, so the large allocation is transient rather
    /// than resident. What stays in memory is roughly a few MB of trimmed records,
    /// which is the whole reason this is a separate switch: on a 2 GB box RAM is
    /// the constraint that matters (see the droplet sizing notes).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, SleeperPlayer>?> FetchPlayersAsync(CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(120); // 5 MB over a small droplet's link

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/players/nfl");
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Sleeper player directory returned {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var map = new Dictionary<string, SleeperPlayer>(StringComparer.Ordinal);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var p = entry.Value;
                if (p.ValueKind != JsonValueKind.Object) continue;

                // full_name is absent for team defences, whose name is the team itself.
                var name = Str(p, "full_name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    var first = Str(p, "first_name");
                    var last = Str(p, "last_name");
                    name = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));
                }
                if (string.IsNullOrWhiteSpace(name)) name = entry.Name;

                map[entry.Name] = new SleeperPlayer(
                    PlayerId: entry.Name,
                    Name: name,
                    Position: Str(p, "position") ?? string.Empty,
                    Team: Str(p, "team") ?? string.Empty);
            }

            _logger.LogInformation("SleeperApiService cached {Count} players", map.Count);
            return map;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sleeper player directory fetch failed");
            return null;
        }
    }

    // ─── HTTP ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One GET returning a parsed document, or null on any failure. Sleeper answers
    /// an unknown league or user with 404 and a null body rather than an error
    /// shape, so "no data" and "bad id" arrive the same way and both land here as
    /// null. Callers phrase that as "not found", never as "the API is down".
    /// </summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
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
                _logger.LogWarning("Sleeper API returned {Status} for {Url}", (int)resp.StatusCode, url);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return null;

            return JsonDocument.Parse(json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sleeper API fetch failed for {Url}", url);
            return null;
        }
    }

    // ─── JSON helpers ────────────────────────────────────────────────────────
    // Sleeper's payloads are loosely typed: fields go missing, come back null, or
    // switch between number and string between endpoints. Everything is read
    // through these so a shape change degrades a field rather than throwing.

    private static JsonElement? Prop(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } p) return null;
        return p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
    }

    private static JsonElement? Prop(JsonElement parent, string name) => Prop((JsonElement?)parent, name);

    private static string? Str(JsonElement? parent, string name)
    {
        var v = Prop(parent, name);
        if (v is null) return null;
        return v.Value.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.ToString(),
            _ => null,
        };
    }

    private static string? Str(JsonElement parent, string name) => Str((JsonElement?)parent, name);

    private static int? Int(JsonElement? parent, string name)
    {
        var v = Prop(parent, name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt32(out var i)) return i;
        if (v.Value.ValueKind == JsonValueKind.String && int.TryParse(v.Value.GetString(), out var s)) return s;
        return null;
    }

    private static int? Int(JsonElement parent, string name) => Int((JsonElement?)parent, name);

    private static double? Dbl(JsonElement? parent, string name)
    {
        var v = Prop(parent, name);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetDouble(out var d)) return d;
        if (v.Value.ValueKind == JsonValueKind.String && double.TryParse(v.Value.GetString(), out var s)) return s;
        return null;
    }

    private static double? Dbl(JsonElement parent, string name) => Dbl((JsonElement?)parent, name);

    /// <summary>
    /// Recombines Sleeper's split points: a whole part plus hundredths in a second
    /// field. Guards the case where the whole field already carries a fraction, in
    /// which case adding the decimal field again would inflate the total.
    /// </summary>
    private static double SplitPoints(JsonElement? settings, string wholeKey, string decimalKey)
    {
        var whole = Dbl(settings, wholeKey) ?? 0;
        if (Math.Abs(whole % 1) > 0.0001) return whole;
        return whole + (Dbl(settings, decimalKey) ?? 0) / 100.0;
    }

    private static IReadOnlyList<string> StringList(JsonElement parent, string name)
    {
        var v = Prop(parent, name);
        if (v is not { ValueKind: JsonValueKind.Array }) return Array.Empty<string>();

        var list = new List<string>();
        foreach (var item in v.Value.EnumerateArray())
        {
            var s = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Number => item.ToString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }

    // ─── Cache slot ──────────────────────────────────────────────────────────

    /// <summary>
    /// One cache entry: fresh within its TTL, refreshed on demand once it expires.
    ///
    /// ── Why this is NOT stale-while-revalidate ──
    /// It was, and that was wrong here. Under stale-while-revalidate an expired
    /// read returns the OLD value and merely kicks off a refresh in the background,
    /// so the caller who triggered the refresh never sees its result. That is fine
    /// for a surface something else polls on a timer, because the next tick is
    /// seconds away. It is wrong for a slash command that gets run twice a day: the
    /// entry is always expired by then, so EVERY invocation serves the previous
    /// invocation's snapshot.
    ///
    /// That shipped, and it bit exactly as described (2026-08-05): a member who had
    /// joined the league the day before was told he was not in it, and running the
    /// same command a second time worked, because the first run had populated the
    /// cache. "Run it twice and it works" is the signature of this bug.
    ///
    /// So an expired read now AWAITS the refresh. Every caller is already off the
    /// gateway thread and past a DeferAsync, which buys 15 minutes, so waiting a
    /// few hundred milliseconds for correct data costs nothing that matters.
    ///
    /// ── What is kept from the old design ──
    /// A refresh that fails or returns nothing leaves the previous value in place
    /// and shortens the TTL rather than clearing it, so one bad minute at Sleeper
    /// degrades to slightly old data instead of blanking the surface. Concurrent
    /// callers share one in-flight refresh rather than each firing their own.
    /// </summary>
    private sealed class CacheSlot<T> where T : class
    {
        /// <summary>
        /// Ceiling on how long a caller waits for a refresh before falling back to
        /// the last good value. Sits above the 20s HTTP timeout so in practice the
        /// fetch always resolves first; this exists only so a wedged task can never
        /// hang a command forever.
        /// </summary>
        private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

        private readonly object _lock = new();
        private T? _value;
        private DateTime _expiresUtc = DateTime.MinValue;
        private Task<T?>? _inFlight;

        public async Task<T?> GetAsync(TimeSpan ttl, TimeSpan failureTtl, Func<Task<T?>> fetch)
        {
            Task<T?> wait;
            T? stale;

            lock (_lock)
            {
                if (DateTime.UtcNow < _expiresUtc && _value is not null) return _value;

                if (_inFlight is null || _inFlight.IsCompleted)
                    _inFlight = RefreshAsync(ttl, failureTtl, fetch);

                wait = _inFlight;
                stale = _value;
            }

            // Expired: wait for the real answer rather than handing back the last
            // one. RefreshAsync never throws and already falls back to the retained
            // value when the fetch fails, so this returns the best available data.
            try
            {
                var done = await Task.WhenAny(wait, Task.Delay(MaxWait));
                if (!ReferenceEquals(done, wait)) return stale; // refresh wedged; last good value beats nothing
                return await wait;
            }
            catch
            {
                return stale;
            }
        }

        private async Task<T?> RefreshAsync(TimeSpan ttl, TimeSpan failureTtl, Func<Task<T?>> fetch)
        {
            T? result = null;
            try { result = await fetch(); }
            catch { /* the fetch logs its own failure; fall through to the retry TTL */ }

            lock (_lock)
            {
                if (result is not null)
                {
                    _value = result;
                    _expiresUtc = DateTime.UtcNow.Add(ttl);
                }
                else
                {
                    _expiresUtc = DateTime.UtcNow.Add(failureTtl);
                }
                return _value;
            }
        }
    }
}
