using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Watches the clan's Satisfactory server by polling QueryServerState and posting a
/// lightweight presence feed to <see cref="BotConfig.SatisfactoryFeedChannelId"/>:
/// server up/down notices, plus a line whenever the connected-player COUNT changes.
///
/// ── Why only a count ──
/// Satisfactory's HTTPS API has no player-list function — it reports
/// NumConnectedPlayers and nothing that names who is on. So this feed can say
/// "3 → 4 players", but never "Alice joined". That's an API limitation, not a
/// design choice, and it's why there's no session table, playtime, or link command
/// here (contrast <see cref="PalworldPresenceService"/>, whose API does list players).
///
/// ── The failure mode this is built around ──
/// A null from <see cref="SatisfactoryApiService.GetServerStateAsync"/> means
/// "unreachable", NOT "empty". Conflating them would fire a fake outage every time
/// the server restarts. So a single failed poll changes nothing; only after
/// <see cref="OfflinePollsBeforeClose"/> consecutive failures do we accept the server
/// is down and post the offline notice.
///
/// ── Restart-safe by omission ──
/// All state is in-memory and starts empty, and up/down is only announced on a real
/// transition (false→true or true→false), never from the null starting verdict. So
/// restarting the bot while the server is already up (or already down) stays quiet —
/// no phantom "server online!" on every deploy.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.SatisfactoryEnabled"/> is true and the API client
/// is configured. The feed is separately gated by
/// <see cref="BotConfig.SatisfactoryFeedEnabled"/> + a channel id.
/// </summary>
public sealed class SatisfactoryPresenceService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Consecutive unreachable polls before we conclude the server is really down.
    /// 5 × the poll interval (default 60s) ≈ 5 minutes, enough to ride out a host
    /// restart or update without posting a spurious outage.
    /// </summary>
    private const int OfflinePollsBeforeClose = 5;

    private readonly DiscordSocketClient _client;
    private readonly SatisfactoryApiService _api;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryPresenceService> _logger;

    private int _consecutiveFailures;

    /// <summary>
    /// Last reachability verdict we ACTED on.
    /// null  = undecided (just booted); true = up; false = announced down.
    /// The null start is what suppresses an "online!" post on every bot restart:
    /// online only fires false→true, offline only true→false.
    /// </summary>
    private bool? _serverOnline;

    /// <summary>
    /// Last player count seen while the server was up; null when unknown/offline.
    /// A change posts a feed line; first contact just records the baseline silently.
    /// </summary>
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
        var state = await _api.GetServerStateAsync(ct);

        // ── Unreachable ──  null is "unknown", never "empty". Hold and wait it out.
        if (state is null)
        {
            _consecutiveFailures++;

            // Fire exactly once, on the poll that crosses the threshold. A blip that
            // recovers sooner produces neither an offline post nor a matching online
            // one — short outages stay silent, which is the whole point of the delay.
            if (_consecutiveFailures == OfflinePollsBeforeClose)
            {
                _logger.LogWarning("Satisfactory server appears offline ({Failures} failed polls)", _consecutiveFailures);

                if (_serverOnline == true)
                    await PostServerStatusAsync(online: false, sessionName: null, ct);

                _serverOnline = false;
                _lastPlayerCount = null;
            }

            return;
        }

        if (_consecutiveFailures > 0)
        {
            _logger.LogInformation("Satisfactory server reachable again after {Failures} failed poll(s)", _consecutiveFailures);
            _consecutiveFailures = 0;
        }

        // Announce recovery only on a real false→true flip. First contact (null) just
        // records the state — the server didn't "come up", it was already up.
        if (_serverOnline == false)
            await PostServerStatusAsync(online: true, sessionName: state.ActiveSessionName, ct);
        _serverOnline = true;

        // ── Player-count feed ──
        var count = state.NumConnectedPlayers;
        if (_lastPlayerCount is int prev && prev != count)
            await PostFeedAsync(CountChangeMessage(prev, count, state), ct);

        _lastPlayerCount = count;
    }

    private static string CountChangeMessage(int prev, int now, SatisfactoryServerState state)
    {
        var arrow = now > prev ? "🟢 ↑" : "🔵 ↓";
        var session = string.IsNullOrWhiteSpace(state.ActiveSessionName) ? "the Satisfactory server" : Escape(state.ActiveSessionName);
        var noun = now == 1 ? "player" : "players";
        return $"{arrow} **{session}** — **{prev} → {now}** {noun} online ({now}/{state.PlayerLimit})";
    }

    private async Task PostFeedAsync(string message, CancellationToken ct)
    {
        if (!_config.SatisfactoryFeedEnabled || _config.SatisfactoryFeedChannelId == 0) return;

        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await channel.SendMessageAsync(
                text: message,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cts.Token });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryPresenceService: failed to post feed message");
        }
    }

    /// <summary>
    /// Posts the whole-server up/down notice as a green/red embed — a rare event,
    /// gated behind the same offline delay as the feed and its own
    /// SatisfactoryServerStatusAnnounceEnabled switch, so up/down pings can be silenced
    /// without losing the count feed. Best-effort — a failed post is logged and dropped.
    /// </summary>
    private async Task PostServerStatusAsync(bool online, string? sessionName, CancellationToken ct)
    {
        if (!_config.SatisfactoryServerStatusAnnounceEnabled) return;
        if (!_config.SatisfactoryFeedEnabled || _config.SatisfactoryFeedChannelId == 0) return;

        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        var embed = new EmbedBuilder()
            .WithColor(online ? Color.Green : Color.Red)
            .WithTitle(online ? "🟢 Satisfactory server is online" : "🔴 Satisfactory server went offline")
            .WithDescription(online
                ? "The clan's Satisfactory server is up — get back to the factory!"
                : "The clan's Satisfactory server stopped responding. It may be restarting or down.")
            .WithCurrentTimestamp();

        if (online && !string.IsNullOrWhiteSpace(sessionName))
            embed.WithFooter($"Session: {sessionName}");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await channel.SendMessageAsync(
                embed: embed.Build(),
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cts.Token });

            _logger.LogInformation("Posted Satisfactory server {State} announcement", online ? "online" : "offline");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryPresenceService: failed to post server {State} announcement",
                online ? "online" : "offline");
        }
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
    /// The session name comes from the server and is builder-controlled text landing
    /// in a Discord message. Neutralize markdown so it can't forge formatting or a
    /// mass-ping. AllowedMentions.None already blocks the ping; this blocks the markup.
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
