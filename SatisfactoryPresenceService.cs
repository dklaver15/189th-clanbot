using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Watches the clan's Satisfactory server and posts a presence feed to
/// <see cref="BotConfig.SatisfactoryFeedChannelId"/>: server up/down/maintenance
/// notices, a line whenever the connected-player COUNT changes, and (via Nitrado) a
/// heads-up before the rental lapses.
///
/// ── Two status sources ──
/// Up/down comes from whichever is available, in priority order:
///   1. Nitrado's host status (when <see cref="BotConfig.NitradoEnabled"/> + a token) —
///      authoritative and, crucially, able to distinguish a planned restart/update
///      (amber "maintenance") from a real outage (red "offline").
///   2. Fallback: the game's own HTTPS API timing out. This can't tell WHY the server
///      is unreachable, so it uses a multi-poll grace before declaring a red outage.
/// Either way the player COUNT always comes from the game API — Nitrado's query count
/// is unreliable for Satisfactory.
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
/// configured. Nitrado is an optional augmentation on top.
/// </summary>
public sealed class SatisfactoryPresenceService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    /// <summary>Only used by the game-API fallback: consecutive unreachable polls before a red outage.</summary>
    private const int OfflinePollsBeforeClose = 5;

    /// <summary>Throttle for the (relatively expensive) rental-expiry check.</summary>
    private static readonly TimeSpan ExpiryCheckInterval = TimeSpan.FromHours(1);

    private enum ServerAvailability { Unknown, Up, Maintenance, Down }

    private readonly DiscordSocketClient _client;
    private readonly SatisfactoryApiService _api;
    private readonly NitradoApiService _nitrado;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryPresenceService> _logger;

    /// <summary>Last availability we ANNOUNCED. Unknown until the first determination.</summary>
    private ServerAvailability _announced = ServerAvailability.Unknown;

    /// <summary>Game-API fallback failure counter (unused while Nitrado is answering).</summary>
    private int _gameApiFailures;

    /// <summary>Last player count seen while up; null when unknown/down.</summary>
    private int? _lastPlayerCount;

    private bool _expiryWarned;
    private DateTime _lastExpiryCheckUtc = DateTime.MinValue;

    public SatisfactoryPresenceService(
        DiscordSocketClient client,
        SatisfactoryApiService api,
        NitradoApiService nitrado,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryPresenceService> logger)
    {
        _client = client;
        _api = api;
        _nitrado = nitrado;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryPollIntervalSeconds, 15, 3600));

    private bool NitradoActive => _config.NitradoEnabled && _nitrado.IsConfigured;

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
            "SatisfactoryPresenceService started; polling every {Seconds}s, feed={Feed} (channel {ChannelId}), nitrado={Nitrado}",
            (int)PollInterval.TotalSeconds, _config.SatisfactoryFeedEnabled, _config.SatisfactoryFeedChannelId, NitradoActive);

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
        var (avail, gs) = await DetermineAvailabilityAsync(ct);

        await HandleAvailabilityAsync(avail, gs, ct);

        // Player-count feed only makes sense while the server is up, and always comes
        // from the game API (Nitrado's count is unreliable for Satisfactory).
        if (avail == ServerAvailability.Up)
            await PollPlayerCountAsync(ct);
        else
            _lastPlayerCount = null;

        if (NitradoActive)
            await MaybeCheckExpiryAsync(ct);
    }

    /// <summary>
    /// Resolves current availability. Prefers Nitrado's authoritative host status;
    /// falls back to probing the game API (with the multi-poll grace) when Nitrado is
    /// off or unreachable. Returns <see cref="ServerAvailability.Unknown"/> to mean
    /// "hold the last announced state" (e.g. within the fallback grace window).
    /// </summary>
    private async Task<(ServerAvailability, NitradoGameServer?)> DetermineAvailabilityAsync(CancellationToken ct)
    {
        if (NitradoActive)
        {
            var gs = await _nitrado.GetGameServerAsync(ct);
            if (gs is not null)
            {
                _gameApiFailures = 0;
                return (MapNitradoStatus(gs.Status), gs);
            }
            // Nitrado unreachable — fall through to the game-API probe rather than go blind.
        }

        var state = await _api.GetServerStateAsync(ct);
        if (state is not null)
        {
            _gameApiFailures = 0;
            return (ServerAvailability.Up, null);
        }

        _gameApiFailures++;
        return _gameApiFailures >= OfflinePollsBeforeClose
            ? (ServerAvailability.Down, null)     // sustained failure: declare a real outage
            : (ServerAvailability.Unknown, null); // brief blip: hold the last state
    }

    private static ServerAvailability MapNitradoStatus(string status)
    {
        var s = (status ?? "").ToLowerInvariant();
        if (s == "started") return ServerAvailability.Up;
        if (s is "stopped" or "suspended" or "deleted" or "error") return ServerAvailability.Down;
        // restarting / stopping / updating / installing / restoring / backup… — a known
        // transient. Treat unrecognized states as maintenance too, to avoid a false red.
        return ServerAvailability.Maintenance;
    }

    /// <summary>Posts the up/down/maintenance embed on a real transition between known states.</summary>
    private async Task HandleAvailabilityAsync(ServerAvailability current, NitradoGameServer? gs, CancellationToken ct)
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
        await PostStatusEmbedAsync(current, gs, ct);
    }

    private async Task PollPlayerCountAsync(CancellationToken ct)
    {
        var state = await _api.GetServerStateAsync(ct);
        if (state is null) return;   // couldn't read this tick; leave the count as-is

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

    // ─── Rental expiry ─────────────────────────────────────────────────────────

    private async Task MaybeCheckExpiryAsync(CancellationToken ct)
    {
        if (_config.NitradoExpiryWarningDays <= 0) return;
        if (DateTime.UtcNow - _lastExpiryCheckUtc < ExpiryCheckInterval) return;
        _lastExpiryCheckUtc = DateTime.UtcNow;

        var svc = await _nitrado.GetServiceAsync(ct);
        if (svc is null) return;

        // Auto-renewing service: nothing to warn about, and re-arm for the future.
        if (svc.AutoExtension || svc.SuspendDate is not DateTimeOffset suspend)
        {
            _expiryWarned = false;
            return;
        }

        var daysLeft = (suspend - DateTimeOffset.UtcNow).TotalDays;
        if (daysLeft > 0 && daysLeft <= _config.NitradoExpiryWarningDays)
        {
            if (!_expiryWarned)
            {
                await PostExpiryWarningAsync(suspend, ct);
                _expiryWarned = true;
            }
        }
        else
        {
            // Renewed (or comfortably far out): re-arm so the next approach warns again.
            _expiryWarned = false;
        }
    }

    private async Task PostExpiryWarningAsync(DateTimeOffset suspend, CancellationToken ct)
    {
        if (!_config.SatisfactoryFeedEnabled || _config.SatisfactoryFeedChannelId == 0) return;
        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        var embed = new EmbedBuilder()
            .WithColor(new Color(0xF1C40F))
            .WithTitle("⚠️ Satisfactory server rental is expiring")
            .WithDescription(
                $"The server's Nitrado rental lapses <t:{suspend.ToUnixTimeSeconds()}:R> " +
                $"(<t:{suspend.ToUnixTimeSeconds()}:f>). Renew it before then to avoid the world going offline.")
            .WithCurrentTimestamp();

        await SendAsync(channel, e => e.Embed = embed.Build(), ct, "expiry warning");
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
    /// Posts the green/amber/red status embed. Gated by
    /// SatisfactoryServerStatusAnnounceEnabled + the feed channel. Best-effort.
    /// </summary>
    private async Task PostStatusEmbedAsync(ServerAvailability state, NitradoGameServer? gs, CancellationToken ct)
    {
        if (!_config.SatisfactoryServerStatusAnnounceEnabled) return;
        if (!_config.SatisfactoryFeedEnabled || _config.SatisfactoryFeedChannelId == 0) return;
        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        var embed = new EmbedBuilder().WithCurrentTimestamp();
        switch (state)
        {
            case ServerAvailability.Up:
                embed.WithColor(Color.Green)
                     .WithTitle("🟢 Satisfactory server is online")
                     .WithDescription("The clan's Satisfactory server is up — get back to the factory!");
                break;

            case ServerAvailability.Maintenance:
                embed.WithColor(new Color(0xF1C40F))
                     .WithTitle("🔧 Satisfactory server is restarting")
                     .WithDescription($"The server is {MaintenanceReason(gs)} — it should be back shortly. Not a crash.");
                break;

            default: // Down
                embed.WithColor(Color.Red)
                     .WithTitle("🔴 Satisfactory server went offline")
                     .WithDescription("The clan's Satisfactory server stopped responding. It may be down or was stopped.");
                break;
        }

        await SendAsync(channel, e => e.Embed = embed.Build(), ct, $"{state} announcement");
    }

    private static string MaintenanceReason(NitradoGameServer? gs)
    {
        var s = (gs?.Status ?? "").ToLowerInvariant();
        var u = (gs?.UpdateStatus ?? "").ToLowerInvariant();
        if (s.Contains("updat") || u.Contains("updat")) return "updating";
        if (s.Contains("install")) return "installing";
        if (s.Contains("restor") || s.Contains("backup")) return "restoring a backup";
        return "restarting";
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
