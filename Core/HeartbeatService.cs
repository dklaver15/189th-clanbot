using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Outbound liveness heartbeat — POSTs (well, GETs; semantics are identical
/// for HC.io) to a configured ping URL every HeartbeatIntervalSeconds.
/// Missed pings trigger an external alert after the grace window configured
/// on the monitor side (e.g. Healthchecks.io).
///
/// ── Why outbound vs. an HTTP /healthz endpoint ──
/// The bot doesn't currently expose any HTTP surface. Adding one would mean
/// standing up Kestrel, mapping a port through docker-compose, and either
/// putting it behind a firewall rule or a token query param. A push-based
/// heartbeat sidesteps all of that — only outbound HTTPS, no inbound surface.
///
/// ── Why we gate on Discord state ──
/// The whole point of monitoring is to catch "bot is not doing its job",
/// not "bot process is alive". A process that's running but disconnected
/// from the Discord gateway (auth failure, gateway stall, network partition
/// to discord.com) is not healthy from the clan's perspective. So we only
/// ping when ConnectionState == Connected AND LoginState == LoggedIn.
///
/// ── Why failures are swallowed ──
/// A failed ping is not a bot failure — it's a monitoring failure. We log
/// it and continue. If pings stop reaching HC.io for any reason (DNS, TLS,
/// HC.io outage), the user will get alerted on their side via the grace
/// window. Throwing here would just take down the loop.
/// </summary>
public class HeartbeatService : BackgroundService
{
    // Dedicated named client so the http factory's per-handler tuning is
    // independent of the other consumers (PhishingDomainFeedService, etc.).
    private const string HttpClientName = "clanguard-heartbeat";

    private readonly DiscordSocketClient _client;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly BotConfig _config;

    public HeartbeatService(
        DiscordSocketClient client,
        IHttpClientFactory httpClientFactory,
        ILogger<HeartbeatService> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.HeartbeatEnabled)
        {
            _logger.LogInformation("HeartbeatService disabled (BotConfig.HeartbeatEnabled=false); exiting");
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.HeartbeatPingUrl))
        {
            _logger.LogWarning(
                "HeartbeatService: HeartbeatEnabled=true but HeartbeatPingUrl is unset. " +
                "Service will exit. Set BotConfig__HeartbeatPingUrl (or CLANGUARD_HEARTBEAT_URL in docker-compose).");
            return;
        }

        // Wait for Discord ready before the first ping so we don't report
        // healthy 5 seconds into startup before we've actually connected.
        // Matches SqliteBackupService's startup wait.
        while (_client.ConnectionState != ConnectionState.Connected
               && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        if (stoppingToken.IsCancellationRequested) return;

        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.HeartbeatIntervalSeconds));
        _logger.LogInformation(
            "HeartbeatService started — pinging every {Seconds}s",
            interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_client.ConnectionState == ConnectionState.Connected
                    && _client.LoginState == LoginState.LoggedIn)
                {
                    await SendHeartbeatAsync(stoppingToken);
                }
                else
                {
                    _logger.LogDebug(
                        "Skipping heartbeat — Discord not ready (ConnectionState={ConnState}, LoginState={LoginState})",
                        _client.ConnectionState, _client.LoginState);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Swallow — see class comment. Monitor-side grace window will
                // alert if this becomes persistent.
                _logger.LogWarning(ex, "Heartbeat ping failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("HeartbeatService stopped");
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient(HttpClientName);
        // 10s timeout is plenty for a healthchecks.io ping and short enough
        // that a stuck request doesn't block the loop for the full interval.
        http.Timeout = TimeSpan.FromSeconds(10);

        using var response = await http.GetAsync(_config.HeartbeatPingUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Heartbeat ping returned non-success status {Status}",
                (int)response.StatusCode);
        }
    }
}
