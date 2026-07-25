using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Watches the clan's Satisfactory server and posts a presence feed to
/// <see cref="BotConfig.SatisfactoryFeedChannelId"/>: server up/down notices and a
/// line whenever the connected-player COUNT changes.
///
/// ── How up/down is decided ──
/// The game's own HTTPS API is the only signal: reachable = up, sustained
/// unreachability = down. That can't distinguish a planned restart from a crash, so
/// a real outage is only declared after <see cref="OfflinePollsBeforeClose"/>
/// consecutive failed polls — short blips stay silent rather than producing an
/// offline/online pair every time the host bounces.
///
/// A Nitrado control-panel client used to supply authoritative host status here,
/// which COULD tell "restarting" apart from "crashed". The clan moved off Nitrado to
/// indifferent broccoli in July 2026 and that code is gone. If the new host ever
/// exposes a panel API, <see cref="DetermineAvailability"/> is where it slots back in.
///
/// ── Why only a count ──
/// Satisfactory's HTTPS API has no player-list function, so this feed reports
/// "3 → 4 players", never "Alice joined". Hence no session table or playtime here.
///
/// ── Quiet on boot ──
/// State is in-memory and starts <see cref="ServerAvailability.Unknown"/>; the first
/// determination is recorded silently. Notices only fire on a real change between
/// known states, so restarting the bot never emits a phantom "online!".
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.SatisfactoryEnabled"/> is true and the game API is
/// configured.
/// </summary>
public sealed class SatisfactoryPresenceService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    /// <summary>Consecutive unreachable polls before a real outage is declared.</summary>
    private const int OfflinePollsBeforeClose = 5;

    private enum ServerAvailability { Unknown, Up, Down }

    private readonly DiscordSocketClient _client;
    private readonly SatisfactoryApiService _api;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryPresenceService> _logger;

    /// <summary>Last availability we ANNOUNCED. Unknown until the first determination.</summary>
    private ServerAvailability _announced = ServerAvailability.Unknown;

    /// <summary>Consecutive failed reads of the game API.</summary>
    private int _gameApiFailures;

    /// <summary>Last player count seen while up; null when unknown/down.</summary>
    private int? _lastPlayerCount;

    public SatisfactoryPresenceService(
        DiscordSocketClient client,
        SatisfactoryApiService api,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryPresenceService> logger)
    {
        _client = client;
        _api = api;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryPollIntervalSeconds, 15, 3600));

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

        _logger.LogInformation(
            "SatisfactoryPresenceService started; polling every {Seconds}s, feed={Feed} (channel {ChannelId})",
            (int)PollInterval.TotalSeconds, _config.SatisfactoryFeedEnabled, _config.SatisfactoryFeedChannelId);

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

    private async Task TickAsync(CancellationToken ct)
    {
        // ONE read per tick: the same server state decides availability and feeds the
        // player-count line. It used to be read twice — once for availability, once in
        // the count poll — which was wasted load on an API that runs on the game thread.
        var state = await _api.GetServerStateAsync(ct);
        var avail = DetermineAvailability(state);

        await HandleAvailabilityAsync(avail, ct);

        // The count feed only makes sense while the server is confirmed up. Anything
        // else (down, or inside the blip grace window) forgets the last count, so
        // recovery doesn't post a bogus "5 → 2" spanning the outage.
        if (avail == ServerAvailability.Up && state is not null)
        {
            var message = TrackPlayerCount(state);
            if (message is not null) await PostFeedAsync(message, ct);
        }
        else
        {
            _lastPlayerCount = null;
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

    /// <summary>Posts the up/down embed on a real transition between known states.</summary>
    private async Task HandleAvailabilityAsync(ServerAvailability current, CancellationToken ct)
    {
        if (current == ServerAvailability.Unknown) return;   // undetermined — hold
        if (current == _announced) return;

        var previous = _announced;
        _announced = current;

        // First contact after boot: record the state, don't announce it (the server
        // didn't just change — we only now looked).
        if (previous == ServerAvailability.Unknown)
        {
            _logger.LogInformation("Satisfactory initial availability: {State}", current);
            return;
        }

        _logger.LogInformation("Satisfactory availability {Prev} → {Now}", previous, current);
        await PostStatusEmbedAsync(current, ct);
    }

    /// <summary>
    /// Records the current player count and returns a feed line if it changed, else
    /// null. Always updates <see cref="_lastPlayerCount"/>, so the bookkeeping can't
    /// be skipped by a caller that decides not to post.
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
        var arrow = now > prev ? "🟢 ↑" : "🔵 ↓";
        var session = string.IsNullOrWhiteSpace(state.ActiveSessionName) ? "the Satisfactory server" : Escape(state.ActiveSessionName);
        var noun = now == 1 ? "player" : "players";
        return $"{arrow} **{session}** — **{prev} → {now}** {noun} online ({now}/{state.PlayerLimit})";
    }

    // ─── Feed posting ───────────────────────────────────────────────────────────

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

    private async Task<IMessageChannel?> ResolveFeedChannelAsync(CancellationToken ct)
    {
        var channel = _client.GetChannel(_config.SatisfactoryFeedChannelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(_config.SatisfactoryFeedChannelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

        if (channel is null)
            _logger.LogWarning("SatisfactoryPresenceService: could not resolve feed channel {ChannelId}",
                _config.SatisfactoryFeedChannelId);

        return channel;
    }

    /// <summary>
    /// The session name is builder-controlled text landing in a Discord message.
    /// Neutralize markdown so it can't forge formatting or a mass-ping.
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
