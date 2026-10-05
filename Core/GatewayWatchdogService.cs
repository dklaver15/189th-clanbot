using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Watchdog for the "process alive but gateway wedged" failure mode — the one
/// where the bot keeps running (background workers tick, the container shows
/// Up) but stops answering interactions, because the Discord gateway connection
/// has silently stalled while still reporting ConnectionState=Connected.
///
/// ── Why ConnectionState isn't enough ──
/// Discord.Net can hold ConnectionState=Connected on a socket that is no longer
/// actually receiving data. Any check that trusts ConnectionState (including the
/// heartbeat's own gate) will keep reporting healthy through this state, so it
/// produces no alert and no recovery. The HeartbeatService gates pings on
/// ConnectionState, which is exactly why a wedge slips past it silently.
///
/// ── The truth signal ──
/// DiscordSocketClient.LatencyUpdated fires on every gateway heartbeat ACK —
/// roughly every 41 seconds — independent of guild traffic. It is the most
/// reliable "the gateway is alive" pulse available without an inbound HTTP
/// surface. If it (and all other inbound events) go quiet past the configured
/// threshold, the gateway is wedged.
///
/// ── Recovery strategy ──
/// Rather than attempt a fragile in-process reconnect on a confused client, the
/// watchdog hard-exits the process. docker-compose restart: unless-stopped then
/// brings it back, and a fresh process always reconnects cleanly (verified). A
/// few seconds of downtime is an acceptable trade for a bulletproof recovery.
///
/// ── Why it can't cause a restart loop on a dead token / Discord outage ──
/// The watchdog only arms after the FIRST successful connection. If the bot can
/// never connect, _everConnected stays false and the watchdog simply waits —
/// it never exits, so a connect failure is left to Discord.Net's own retry and
/// the operator, not turned into a crash loop.
/// </summary>
public class GatewayWatchdogService : BackgroundService
{
    private readonly DiscordSocketClient _client;
    private readonly ILogger<GatewayWatchdogService> _logger;
    private readonly BotConfig _config;

    // Last time we saw any sign of gateway life, as UTC ticks. Accessed from
    // both the event-handler callbacks and the check loop, so reads/writes go
    // through Interlocked to stay atomic.
    private long _lastActivityTicks;

    private volatile bool _everConnected;

    public GatewayWatchdogService(
        DiscordSocketClient client,
        ILogger<GatewayWatchdogService> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GatewayWatchdogEnabled)
        {
            _logger.LogInformation(
                "GatewayWatchdogService disabled (BotConfig.GatewayWatchdogEnabled=false); exiting");
            return;
        }

        MarkActivity();

        _client.LatencyUpdated  += OnLatencyUpdated;
        _client.Connected       += OnConnected;
        _client.Ready           += OnReady;
        _client.MessageReceived += OnMessageReceived;

        var stallThreshold = TimeSpan.FromSeconds(Math.Max(60, _config.GatewayWatchdogStallSeconds));
        var checkInterval  = TimeSpan.FromSeconds(Math.Max(15, _config.GatewayWatchdogCheckSeconds));

        _logger.LogInformation(
            "GatewayWatchdogService started — stall threshold {Stall:F0}s, checking every {Check:F0}s",
            stallThreshold.TotalSeconds, checkInterval.TotalSeconds);

        try
        {
            // Arm only after the first real connection so a slow startup never
            // trips the watchdog before we've ever been online. ExecuteAsync can
            // start after Connected/Ready already fired, so also check the state.
            while (!_everConnected
                   && _client.ConnectionState != ConnectionState.Connected
                   && !stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(checkInterval, stoppingToken);

                var last = new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
                var idle = DateTime.UtcNow - last;

                if (idle >= stallThreshold)
                {
                    _logger.LogCritical(
                        "GATEWAY WATCHDOG: no gateway activity for {Idle:F0}s (threshold {Stall:F0}s, " +
                        "ConnectionState={State}). Gateway is wedged — exiting so Docker restarts the " +
                        "process for a clean reconnect.",
                        idle.TotalSeconds, stallThreshold.TotalSeconds, _client.ConnectionState);

                    // Let the log line flush, then hard-exit. restart:
                    // unless-stopped in docker-compose brings the bot back.
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    Environment.Exit(1);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        finally
        {
            _client.LatencyUpdated  -= OnLatencyUpdated;
            _client.Connected       -= OnConnected;
            _client.Ready           -= OnReady;
            _client.MessageReceived -= OnMessageReceived;
        }

        _logger.LogInformation("GatewayWatchdogService stopped");
    }

    private void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    // LatencyUpdated is the primary pulse (~every 41s); the rest catch connect/
    // reconnect transitions and real traffic so any sign of life resets idle.
    private Task OnLatencyUpdated(int oldLatency, int newLatency) { MarkActivity(); return Task.CompletedTask; }
    private Task OnConnected() { _everConnected = true; MarkActivity(); return Task.CompletedTask; }
    private Task OnReady() { _everConnected = true; MarkActivity(); return Task.CompletedTask; }
    private Task OnMessageReceived(SocketMessage message) { MarkActivity(); return Task.CompletedTask; }
}
