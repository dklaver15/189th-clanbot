using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Watches the clan's Satisfactory server and posts a presence feed to
/// <see cref="BotConfig.SatisfactoryFeedChannelId"/>: server up/down notices, and
/// either named join/leave lines (when the Ficsit Remote Monitoring mod is
/// available) or a bare player-count line (when it isn't).
///
/// ── Two sources, two jobs ──
/// <see cref="SatisfactoryApiService"/> (the game's own HTTPS API) decides
/// whether the server is UP. It's authoritative for that and nothing else can
/// replace it — but it exposes only a player COUNT, never a name.
/// <see cref="FrmApiService"/> (the FRM mod) supplies the names, and therefore
/// the sessions, playtime and join/leave feed. It's optional: with FRM off or
/// unreachable, this degrades to the count feed it always had.
///
/// ── How up/down is decided ──
/// Reachable = up, sustained unreachability = down. That can't distinguish a
/// planned restart from a crash, so a real outage is only declared after
/// <see cref="OfflinePollsBeforeClose"/> consecutive failed polls — short blips
/// stay silent rather than producing an offline/online pair every time the host
/// bounces. (A Nitrado panel client used to give authoritative host status here;
/// the clan moved to indifferent broccoli in July 2026 and it was removed.
/// <see cref="DetermineAvailability"/> is where a replacement would slot in.)
///
/// ── Sessions are keyed on the player NAME ──
/// See <see cref="SatisfactorySession"/> for why: the character actor id resets
/// on every new save, and the clan already wiped one moving hosts.
///
/// ── Quiet on boot ──
/// Availability starts <see cref="ServerAvailability.Unknown"/> and the first
/// determination is recorded silently, so restarting the bot never emits a
/// phantom "online!". Open sessions left by the previous run are ADOPTED rather
/// than restarted, so a restart doesn't re-announce everyone as joining or
/// double-count their time.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.SatisfactoryEnabled"/> is true and the game
/// API is configured. FRM is a further, independent opt-in.
/// </summary>
public sealed class SatisfactoryPresenceService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    /// <summary>Consecutive unreachable polls before a real outage is declared.</summary>
    private const int OfflinePollsBeforeClose = 5;

    /// <summary>
    /// How stale LastSeenUtc may be before a leave is credited at
    /// LastSeenUtc instead of now. Three polls: long enough that an ordinary
    /// tick is never misread as an outage, short enough that a real gap can't
    /// quietly become playtime.
    /// </summary>
    private TimeSpan StaleSessionGrace => PollInterval * 3;

    private enum ServerAvailability { Unknown, Up, Down }

    private readonly DiscordSocketClient _client;
    private readonly SatisfactoryApiService _api;
    private readonly FrmApiService _frm;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryPresenceService> _logger;

    /// <summary>Last availability we ANNOUNCED. Unknown until the first determination.</summary>
    private ServerAvailability _announced = ServerAvailability.Unknown;

    /// <summary>Consecutive failed reads of the game API.</summary>
    private int _gameApiFailures;

    /// <summary>Last player count seen while up; null when unknown/down. Only used in the FRM-less fallback.</summary>
    private int? _lastPlayerCount;

    /// <summary>
    /// Who we currently believe is online, keyed by player name.
    ///
    /// Ordinal (case-SENSITIVE) on purpose: these keys are compared against
    /// SQLite text columns, whose default equality is also case-sensitive.
    /// Matching in-memory case-insensitively while the database matches
    /// case-sensitively would let a name that differs only in case create a
    /// second session row while looking like the same person here.
    /// </summary>
    private readonly Dictionary<string, FrmPlayer> _online = new(StringComparer.Ordinal);

    public SatisfactoryPresenceService(
        DiscordSocketClient client,
        SatisfactoryApiService api,
        FrmApiService frm,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryPresenceService> logger)
    {
        _client = client;
        _api = api;
        _frm = frm;
        _services = services;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryPollIntervalSeconds, 15, 3600));

    private bool FrmActive => _config.FrmEnabled && _frm.IsConfigured;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!_config.SatisfactoryEnabled || !_api.IsConfigured)
        {
            _logger.LogInformation(
                "SatisfactoryPresenceService idle (enabled={Enabled}, configured={Configured})",
                _config.SatisfactoryEnabled, _api.IsConfigured);
            return;
        }

        if (FrmActive)
        {
            try
            {
                await AdoptOpenSessionsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A failed adopt would make the first tick re-announce everyone as
                // joining. Log and continue rather than killing the poller.
                _logger.LogError(ex, "SatisfactoryPresenceService: failed to adopt open sessions on boot");
            }
        }

        _logger.LogInformation(
            "SatisfactoryPresenceService started; polling every {Seconds}s, feed={Feed} (channel {ChannelId}), frm={Frm}",
            (int)PollInterval.TotalSeconds, _config.SatisfactoryFeedEnabled,
            _config.SatisfactoryFeedChannelId, FrmActive);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SatisfactoryPresenceService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Rebuilds the in-memory online set from the open sessions left behind by
    /// the previous run, so a restart doesn't re-announce everyone as joining.
    /// The rows are trusted as-is; the first poll immediately corrects them
    /// (anyone who actually left gets their session closed at LastSeenUtc).
    /// </summary>
    private async Task AdoptOpenSessionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var open = await db.SatisfactorySessions
            .Where(s => s.EndedUtc == null)
            .ToListAsync(ct);

        foreach (var s in open)
        {
            if (string.IsNullOrEmpty(s.PlayerName)) continue;
            _online[s.PlayerName] = new FrmPlayer(
                Id: s.PlayerId,
                Name: s.PlayerName,
                Online: true,
                Dead: false,
                PlayerHP: 0,
                Location: null);
        }

        if (open.Count > 0)
            _logger.LogInformation("Satisfactory: adopted {Count} open session(s) from the previous run", open.Count);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // ONE game-API read per tick: the same state decides availability and
        // feeds the count fallback.
        var state = await _api.GetServerStateAsync(ct);
        var avail = DetermineAvailability(state);

        await HandleAvailabilityAsync(avail, ct);

        if (avail != ServerAvailability.Up || state is null)
        {
            // Down, or inside the blip grace window. Forget the last count so
            // recovery can't post a bogus "5 → 2" spanning the outage. Sessions
            // are handled by the Up→Down transition, not here — a blip must not
            // close anything.
            _lastPlayerCount = null;
            return;
        }

        if (FrmActive)
        {
            // Names supersede the count line entirely — posting both would say
            // the same thing twice.
            await PollPlayersAsync(ct);
        }
        else
        {
            var message = TrackPlayerCount(state);
            if (message is not null) await PostFeedAsync(message, ct);
        }
    }

    /// <summary>
    /// Maps a server-state read to availability. Returns
    /// <see cref="ServerAvailability.Unknown"/> to mean "hold the last announced
    /// state" — used inside the grace window, so a brief blip announces nothing.
    /// </summary>
    private ServerAvailability DetermineAvailability(SatisfactoryServerState? state)
    {
        if (state is not null)
        {
            _gameApiFailures = 0;
            return ServerAvailability.Up;
        }

        _gameApiFailures++;
        return _gameApiFailures >= OfflinePollsBeforeClose
            ? ServerAvailability.Down      // sustained failure: declare a real outage
            : ServerAvailability.Unknown;  // brief blip: hold the last state
    }

    /// <summary>
    /// Posts the up/down embed on a real transition between known states, and
    /// closes out open sessions when the server goes down.
    /// </summary>
    private async Task HandleAvailabilityAsync(ServerAvailability current, CancellationToken ct)
    {
        if (current == ServerAvailability.Unknown) return;   // undetermined — hold
        if (current == _announced) return;

        var previous = _announced;
        _announced = current;

        // Going down closes every open session at its own LastSeenUtc — whether
        // or not we'd announced an up state before, because the rows may have
        // been adopted from a previous run. Deliberately silent: the players
        // didn't leave, we lost visibility, and a burst of leave lines for a
        // server restart is exactly the noise this feature must not produce.
        if (current == ServerAvailability.Down && FrmActive)
            await CloseAllOpenSessionsAsync(ct);

        // First contact after boot: record the state, don't announce it (the
        // server didn't just change — we only now looked).
        if (previous == ServerAvailability.Unknown)
        {
            _logger.LogInformation("Satisfactory initial availability: {State}", current);
            return;
        }

        _logger.LogInformation("Satisfactory availability {Prev} → {Now}", previous, current);
        await PostStatusEmbedAsync(current, ct);
    }

    // ─── Named presence (FRM) ───────────────────────────────────────────────

    /// <summary>
    /// Diffs the live player list against what we last saw, writes session rows,
    /// and posts a join/leave line per change.
    /// </summary>
    private async Task PollPlayersAsync(CancellationToken ct)
    {
        var players = await _frm.GetOnlinePlayersAsync(ct);

        // null is "unknown", never "empty". FRM can be down while the game API is
        // fine — most likely its web server failed to bind its port — and that
        // must not read as everyone leaving.
        if (players is null) return;

        var seen = new Dictionary<string, FrmPlayer>(StringComparer.Ordinal);
        foreach (var p in players)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;   // unnamed = unusable as a key
            seen[p.Name] = p;
        }

        var joined = seen.Where(kv => !_online.ContainsKey(kv.Key)).ToList();
        var left = _online.Where(kv => !seen.ContainsKey(kv.Key)).ToList();

        if (joined.Count == 0 && left.Count == 0)
        {
            // Steady state: still refresh the open rows so LastSeenUtc tracks
            // reality. This is what makes an open session's duration trustworthy.
            if (seen.Count > 0) await TouchOpenSessionsAsync(seen, ct);
            return;
        }

        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Discord links for whoever changed this tick, so the feed can name the
        // member rather than just the character. Mentions render without pinging
        // because every send uses AllowedMentions.None.
        var affected = joined.Select(kv => kv.Key).Concat(left.Select(kv => kv.Key)).Distinct().ToList();
        // Grouped rather than ToDictionaryAsync: SatisfactoryLink is unique on
        // (GuildId, PlayerName), not on PlayerName alone, so if the bot is ever
        // in two guilds and one in-game name is linked in both, ToDictionary
        // throws "an item with the same key has already been added" — out of the
        // tick, on every poll, for as long as both links exist.
        var links = (await db.SatisfactoryLinks
                .Where(l => affected.Contains(l.SatisfactoryPlayerName))
                .Select(l => new { l.SatisfactoryPlayerName, l.DiscordUserId })
                .ToListAsync(ct))
            .GroupBy(l => l.SatisfactoryPlayerName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DiscordUserId, StringComparer.Ordinal);

        // Feed lines are collected, NOT posted, while the transaction is open —
        // see the ordering note below.
        var feed = new List<string>();

        // ── Leaves ──
        foreach (var (name, _) in left)
        {
            var session = await db.SatisfactorySessions
                .Where(s => s.EndedUtc == null && s.PlayerName == name)
                .OrderByDescending(s => s.StartedUtc)
                .FirstOrDefaultAsync(ct);

            TimeSpan? played = null;
            if (session is not null)
            {
                // Close at the last time we actually OBSERVED them, not now.
                //
                // LastSeenUtc is normally one poll old, and the two are then
                // interchangeable. But it can be hours stale in two reachable
                // ways, and in both of them "now" invents playtime nobody had:
                //
                //   • The bot restarted while they were online and they logged
                //     off during the outage. AdoptOpenSessionsAsync re-adopts
                //     the row with its old LastSeenUtc, and the first poll sees
                //     them missing.
                //   • FRM went down while the game API stayed up — its web
                //     server failing to bind its port is a recurring condition
                //     on this host. PollPlayersAsync returns early so
                //     LastSeenUtc stops advancing, while availability never goes
                //     Down, so CloseAllOpenSessionsAsync never runs.
                //
                // Left unclamped, a 3-day outage becomes a 3-day session — and
                // because the daily digest clips sessions to each day's window,
                // that one row would report 24h of play on every intervening
                // day, including days nobody logged on.
                var observedEnd = now - session.LastSeenUtc > StaleSessionGrace
                    ? session.LastSeenUtc
                    : now;

                session.EndedUtc = observedEnd;
                session.LastSeenUtc = observedEnd;
                played = session.Duration;
            }

            feed.Add(LeaveMessage(name, links, played, seen.Count));
        }

        // ── Joins ──
        foreach (var (name, player) in joined)
        {
            db.SatisfactorySessions.Add(new SatisfactorySession
            {
                PlayerName = name,
                PlayerId = player.Id,
                StartedUtc = now,
                LastSeenUtc = now,
                EndedUtc = null,
            });

            feed.Add(JoinMessage(name, links, seen.Count));
        }

        // ── Commit, THEN advance in-memory state, THEN talk to Discord ──
        //
        // The previous order mutated _online and awaited a Discord REST call in
        // the middle of an open transaction, so any failure after the first
        // mutation left the two permanently disagreeing:
        //
        //   • a leave that updated _online but not the row left the session open
        //     forever — the player is gone from _online so they never appear in
        //     `left` again, and gone from `seen` so TouchOpenSessionsAsync never
        //     bumps them. They read as online in /satisfactory-leaderboard
        //     indefinitely, and the next restart re-adopts the zombie row.
        //   • a join that updated _online but not the row dropped the whole
        //     stint, and couldn't self-heal for the same reason.
        //
        // Saving first means a throw leaves _online untouched, so the very next
        // poll recomputes the identical diff and retries it.
        await db.SaveChangesAsync(ct);

        foreach (var (name, _) in left) _online.Remove(name);
        foreach (var (name, player) in joined) _online[name] = player;

        foreach (var message in feed)
            await PostFeedAsync(message, ct);

        // Refresh the survivors' rows in the same pass.
        if (seen.Count > 0) await TouchOpenSessionsAsync(seen, ct);
    }

    /// <summary>
    /// Bumps LastSeenUtc on every open session for a player we can still see,
    /// and refreshes the recorded character id in case the save changed.
    /// </summary>
    private async Task TouchOpenSessionsAsync(Dictionary<string, FrmPlayer> seen, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var open = await db.SatisfactorySessions
            .Where(s => s.EndedUtc == null)
            .ToListAsync(ct);
        if (open.Count == 0) return;

        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var s in open)
        {
            if (!seen.TryGetValue(s.PlayerName, out var p)) continue;

            s.LastSeenUtc = now;
            if (!string.IsNullOrEmpty(p.Id)) s.PlayerId = p.Id;
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Closes every open session at its own LastSeenUtc — used when the server
    /// has been unreachable long enough to be considered down. Credits only what
    /// was actually observed, never the outage.
    /// </summary>
    private async Task CloseAllOpenSessionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var open = await db.SatisfactorySessions
            .Where(s => s.EndedUtc == null)
            .ToListAsync(ct);

        foreach (var s in open)
            s.EndedUtc = s.LastSeenUtc;

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Satisfactory server unreachable; closed {Count} open session(s) at their last-seen time", open.Count);
        }

        _online.Clear();
    }

    // ─── Message shaping ────────────────────────────────────────────────────

    /// <summary>
    /// "**Brandt** (@Dan)" when linked, else just the escaped name. The mention
    /// is safe to include because sends use AllowedMentions.None.
    /// </summary>
    private static string FeedName(string name, IReadOnlyDictionary<string, ulong> links) =>
        links.TryGetValue(name, out var discordId)
            ? $"**{Escape(name)}** (<@{discordId}>)"
            : $"**{Escape(name)}**";

    private static string JoinMessage(string name, IReadOnlyDictionary<string, ulong> links, int onlineCount) =>
        $"🟢 {FeedName(name, links)} joined the factory — {onlineCount} online";

    private static string LeaveMessage(string name, IReadOnlyDictionary<string, ulong> links, TimeSpan? played, int onlineCount) =>
        $"🔴 {FeedName(name, links)} left" +
        (played is TimeSpan d && d > TimeSpan.Zero ? $" after {Humanize(d)}" : "") +
        $" — {onlineCount} online";

    // ─── Count fallback (no FRM) ────────────────────────────────────────────

    /// <summary>
    /// Records the current player count and returns a feed line if it changed,
    /// else null. Only used when FRM is unavailable — with names, this is noise.
    /// </summary>
    private string? TrackPlayerCount(SatisfactoryServerState state)
    {
        var count = state.NumConnectedPlayers;

        string? message = null;
        if (_lastPlayerCount is int prev && prev != count)
            message = CountChangeMessage(prev, count, state);

        _lastPlayerCount = count;
        return message;
    }

    private static string CountChangeMessage(int prev, int now, SatisfactoryServerState state)
    {
        var arrow = now > prev ? "🟢 ↑" : "🔴 ↓";
        var session = string.IsNullOrWhiteSpace(state.ActiveSessionName) ? "the Satisfactory server" : Escape(state.ActiveSessionName);
        var noun = now == 1 ? "player" : "players";
        return $"{arrow} **{session}** — **{prev} → {now}** {noun} online ({now}/{state.PlayerLimit})";
    }

    // ─── Feed posting ───────────────────────────────────────────────────────

    private async Task PostFeedAsync(string message, CancellationToken ct)
    {
        if (!_config.SatisfactoryFeedEnabled || _config.SatisfactoryFeedChannelId == 0) return;
        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        await SendAsync(channel, e => e.Text = message, ct, "feed message");
    }

    /// <summary>
    /// Posts the green/red status embed. Gated by
    /// SatisfactoryServerStatusAnnounceEnabled + the feed channel. Best-effort.
    /// </summary>
    private async Task PostStatusEmbedAsync(ServerAvailability state, CancellationToken ct)
    {
        if (!_config.SatisfactoryServerStatusAnnounceEnabled) return;
        if (!_config.SatisfactoryFeedEnabled || _config.SatisfactoryFeedChannelId == 0) return;
        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        var embed = new EmbedBuilder().WithCurrentTimestamp();

        if (state == ServerAvailability.Up)
        {
            embed.WithColor(Color.Green)
                 .WithTitle("🟢 Satisfactory server is online")
                 .WithDescription("The clan's Satisfactory server is up — get back to the factory!");
        }
        else
        {
            embed.WithColor(Color.Red)
                 .WithTitle("🔴 Satisfactory server went offline")
                 .WithDescription(
                     "The clan's Satisfactory server stopped responding. It may have crashed, been " +
                     "stopped, or be mid-restart — without a host panel API this feed can't tell " +
                     "those apart.");
        }

        await SendAsync(channel, e => e.Embed = embed.Build(), ct, $"{state} announcement");
    }

    /// <summary>One place for the send + timeout + swallow-and-log pattern.</summary>
    private async Task SendAsync(IMessageChannel channel, Action<SendSpec> configure, CancellationToken ct, string what)
    {
        var spec = new SendSpec();
        configure(spec);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await channel.SendMessageAsync(
                text: spec.Text,
                embed: spec.Embed,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cts.Token });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryPresenceService: failed to post {What}", what);
        }
    }

    private sealed class SendSpec
    {
        public string? Text { get; set; }
        public Embed? Embed { get; set; }
    }

    /// <summary>
    /// Resolves the feed channel, returning null on any failure.
    ///
    /// <para>The REST fallback is a network call and CAN throw — a 403 after a
    /// permission change, a 5xx, an exhausted rate-limit retry. It used to throw
    /// straight through PollPlayersAsync, which is the one caller that holds
    /// mutable state mid-update. Swallowing here keeps a Discord hiccup from
    /// costing a session row; SendAsync already swallows on the same principle.</para>
    /// </summary>
    private async Task<IMessageChannel?> ResolveFeedChannelAsync(CancellationToken ct)
    {
        try
        {
            var channel = _client.GetChannel(_config.SatisfactoryFeedChannelId) as IMessageChannel
                          ?? await _client.Rest.GetChannelAsync(_config.SatisfactoryFeedChannelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

            if (channel is null)
                _logger.LogWarning("SatisfactoryPresenceService: could not resolve feed channel {ChannelId}",
                    _config.SatisfactoryFeedChannelId);

            return channel;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryPresenceService: failed to resolve feed channel {ChannelId}",
                _config.SatisfactoryFeedChannelId);
            return null;
        }
    }

    /// <summary>"2h 14m" / "47m". Shared with SatisfactoryCommandHandler.</summary>
    public static string Humanize(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return "under a minute";
        var days = (int)d.TotalDays;
        var hours = d.Hours;
        var minutes = d.Minutes;
        if (days > 0) return $"{days}d {hours}h";
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    /// <summary>
    /// Player and session names are builder-controlled text landing in a Discord
    /// message. Neutralize markdown so they can't forge formatting or a mass-ping.
    /// </summary>
    private static string Escape(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "the Satisfactory server"
            : s.Replace("\\", "\\\\")
               .Replace("*", "\\*")
               .Replace("_", "\\_")
               .Replace("~", "\\~")
               .Replace("`", "\\`")
               .Replace("|", "\\|")
               .Replace("@", "@​")
               .Replace("\n", " ")
               .Replace("\r", " ");
}
