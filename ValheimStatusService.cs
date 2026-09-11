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
/// Polls the clan's Valheim server over A2S and posts an up/down + player-count
/// feed to <see cref="BotConfig.ValheimFeedChannelId"/>, recording every reachable
/// poll as a <see cref="ValheimMetricSample"/>.
///
/// ── Why this is the count feed and not a presence feed ──
/// <see cref="SatisfactoryPresenceService"/> has two modes: named join/leave when
/// the FRM mod is answering, and a bare count line when it isn't. This service only
/// ever has the second one, because A2S cannot name players (see
/// <see cref="ValheimQueryService"/>). There are deliberately no session rows, no
/// playtime and no link table here — those need DiscordConnector on the server, and
/// a half-built version keyed on a count would produce numbers nobody could trust.
///
/// ── How up/down is decided ──
/// Reachable = up; sustained unreachability = down. UDP has no delivery guarantee,
/// so a single silent poll is meaningless and only
/// <see cref="OfflinePollsBeforeDown"/> consecutive failures declare an outage —
/// otherwise the feed would flap on ordinary packet loss. This cannot tell a crash
/// from a planned restart or a firewalled query port; the announcement says so
/// rather than guessing.
///
/// ── Quiet on boot ──
/// Availability starts Unknown and the first determination is recorded silently, so
/// restarting the bot never emits a phantom "online!" for a server that was already
/// up. Same rule as the Palworld and Satisfactory pollers.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.ValheimEnabled"/> is true and a host is set.
/// Three independent switches beyond that, so each kind of noise can be turned off
/// on its own: <see cref="BotConfig.ValheimFeedEnabled"/> (per-player chatter),
/// <see cref="BotConfig.ValheimServerStatusAnnounceEnabled"/> (up/down embeds), and
/// <see cref="BotConfig.ValheimMetricsSamplingEnabled"/> (history). All three read
/// the same poll, and history in particular runs with both feeds silent.
/// </summary>
public sealed class ValheimStatusService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Consecutive unreachable polls before an outage is declared. Five — five
    /// minutes at the default cadence — rides out packet loss and a Shockbyte
    /// restart without announcing anything.
    /// </summary>
    private const int OfflinePollsBeforeDown = 5;

    /// <summary>How often to sweep expired samples. Cheap, so rarely.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    private enum ServerAvailability { Unknown, Up, Down }

    private readonly DiscordSocketClient _client;
    private readonly ValheimQueryService _query;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<ValheimStatusService> _logger;

    /// <summary>Last availability we ANNOUNCED. Unknown until the first determination.</summary>
    private ServerAvailability _announced = ServerAvailability.Unknown;

    /// <summary>Consecutive failed queries.</summary>
    private int _failures;

    /// <summary>
    /// Last count seen while up; null when down or unknown. Cleared across an
    /// outage so recovery can't post a "5 → 2" line spanning the downtime.
    /// </summary>
    private int? _lastPlayerCount;

    private DateTime _lastPruneUtc = DateTime.MinValue;

    public ValheimStatusService(
        DiscordSocketClient client,
        ValheimQueryService query,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<ValheimStatusService> logger)
    {
        _client = client;
        _query = query;
        _services = services;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.ValheimPollIntervalSeconds, 15, 3600));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!_config.ValheimEnabled || !_query.IsConfigured)
        {
            _logger.LogInformation(
                "ValheimStatusService idle (enabled={Enabled}, configured={Configured})",
                _config.ValheimEnabled, _query.IsConfigured);
            return;
        }

        _logger.LogInformation(
            "ValheimStatusService started; querying {Host}:{QueryPort} every {Seconds}s, feed={Feed} (channel {ChannelId})",
            _config.ValheimHost, _query.QueryPort, (int)PollInterval.TotalSeconds,
            _config.ValheimFeedEnabled, _config.ValheimFeedChannelId);

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
                _logger.LogError(ex, "ValheimStatusService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // ONE query per tick: the same reading decides availability, drives the
        // count feed and becomes the sample.
        var info = await _query.QueryAsync(ct);
        var avail = DetermineAvailability(info);

        await HandleAvailabilityAsync(avail, ct);

        if (info is null)
        {
            // Unreachable, or inside the blip grace window. Drop the baseline so
            // the next successful poll can't report a change across the gap.
            _lastPlayerCount = null;
            return;
        }

        var message = TrackPlayerCount(info);
        if (message is not null) await PostFeedAsync(message, ct);

        await RecordSampleAsync(info, ct);
    }

    /// <summary>
    /// Maps a query result to availability. Returns
    /// <see cref="ServerAvailability.Unknown"/> to mean "hold the last announced
    /// state", which is what keeps a dropped datagram silent.
    /// </summary>
    private ServerAvailability DetermineAvailability(ValheimServerInfo? info)
    {
        if (info is not null)
        {
            _failures = 0;
            return ServerAvailability.Up;
        }

        _failures++;
        return _failures >= OfflinePollsBeforeDown
            ? ServerAvailability.Down      // sustained silence: a real outage
            : ServerAvailability.Unknown;  // brief blip: hold
    }

    private async Task HandleAvailabilityAsync(ServerAvailability current, CancellationToken ct)
    {
        if (current == ServerAvailability.Unknown) return;
        if (current == _announced) return;

        var previous = _announced;
        _announced = current;

        // First contact after boot: record it, don't announce it. The server
        // didn't just change state — we only now looked at it.
        if (previous == ServerAvailability.Unknown)
        {
            _logger.LogInformation("Valheim initial availability: {State}", current);
            return;
        }

        _logger.LogInformation("Valheim availability {Prev} → {Now}", previous, current);
        await PostStatusEmbedAsync(current, ct);
    }

    // ─── Count feed ─────────────────────────────────────────────────────────

    /// <summary>
    /// Records the current count and returns a feed line when it changed, else null.
    /// </summary>
    private string? TrackPlayerCount(ValheimServerInfo info)
    {
        string? message = null;
        if (_lastPlayerCount is int prev && prev != info.Players)
            message = CountChangeMessage(prev, info);

        _lastPlayerCount = info.Players;
        return message;
    }

    private static string CountChangeMessage(int prev, ValheimServerInfo info)
    {
        var arrow = info.Players > prev ? "🟢 ↑" : "🔴 ↓";
        var world = string.IsNullOrWhiteSpace(info.World) ? "the Valheim server" : Escape(info.World);
        var noun = info.Players == 1 ? "player" : "players";
        return $"{arrow} **{world}** — **{prev} → {info.Players}** {noun} online ({info.Players}/{info.MaxPlayers})";
    }

    // ─── History ────────────────────────────────────────────────────────────

    private async Task RecordSampleAsync(ValheimServerInfo info, CancellationToken ct)
    {
        if (!_config.ValheimMetricsSamplingEnabled) return;

        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        db.ValheimMetricSamples.Add(new ValheimMetricSample
        {
            SampledUtc = now,
            PlayerCount = info.Players,
            MaxPlayerCount = info.MaxPlayers,
            World = info.World,
            RoundTripMs = (int)Math.Clamp(info.RoundTrip.TotalMilliseconds, 0, int.MaxValue),
        });

        await db.SaveChangesAsync(ct);
        await PruneOldSamplesAsync(db, now, ct);
    }

    private async Task PruneOldSamplesAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        if (_config.ValheimMetricsRetentionDays <= 0) return;
        if (now - _lastPruneUtc < PruneInterval) return;

        _lastPruneUtc = now;

        var cutoff = now.AddDays(-_config.ValheimMetricsRetentionDays);
        var stale = await db.ValheimMetricSamples
            .Where(s => s.SampledUtc < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        db.ValheimMetricSamples.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Pruned {Count} Valheim sample(s) older than {Days}d",
            stale.Count, _config.ValheimMetricsRetentionDays);
    }

    // ─── Posting ────────────────────────────────────────────────────────────

    private async Task PostFeedAsync(string message, CancellationToken ct)
    {
        if (!_config.ValheimFeedEnabled || _config.ValheimFeedChannelId == 0) return;
        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        await SendAsync(channel, e => e.Text = message, ct, "feed message");
    }

    /// <summary>
    /// Posts the green/red status embed.
    ///
    /// <para>Gated by <see cref="BotConfig.ValheimServerStatusAnnounceEnabled"/> and a
    /// channel id, but deliberately NOT by
    /// <see cref="BotConfig.ValheimFeedEnabled"/> — which is a considered divergence
    /// from <see cref="SatisfactoryPresenceService"/>, where the feed flag gates both.
    /// An outage notice and per-player chatter are wanted at completely different
    /// rates: "tell me when the server dies, don't tell me every time someone logs
    /// in" is the common preference, and under the shared gate it was not
    /// expressible at all.</para>
    /// </summary>
    private async Task PostStatusEmbedAsync(ServerAvailability state, CancellationToken ct)
    {
        if (!_config.ValheimServerStatusAnnounceEnabled) return;
        if (_config.ValheimFeedChannelId == 0) return;
        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        var embed = new EmbedBuilder().WithCurrentTimestamp();

        if (state == ServerAvailability.Up)
        {
            embed.WithColor(Color.Green)
                 .WithTitle("🟢 Valheim server is online")
                 .WithDescription($"The clan's Valheim server is up — `{_query.JoinAddress}`");
        }
        else
        {
            embed.WithColor(Color.Red)
                 .WithTitle("🔴 Valheim server went offline")
                 .WithDescription(
                     "The clan's Valheim server stopped answering. It may have crashed, been stopped, " +
                     "or be mid-restart — the Steam query protocol can't tell those apart.");
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
            _logger.LogWarning(ex, "ValheimStatusService: failed to post {What}", what);
        }
    }

    private sealed class SendSpec
    {
        public string? Text { get; set; }
        public Embed? Embed { get; set; }
    }

    /// <summary>Resolves the feed channel, returning null on any failure.</summary>
    private async Task<IMessageChannel?> ResolveFeedChannelAsync(CancellationToken ct)
    {
        try
        {
            var channel = _client.GetChannel(_config.ValheimFeedChannelId) as IMessageChannel
                          ?? await _client.Rest.GetChannelAsync(_config.ValheimFeedChannelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

            if (channel is null)
                _logger.LogWarning("ValheimStatusService: could not resolve feed channel {ChannelId}",
                    _config.ValheimFeedChannelId);

            return channel;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ValheimStatusService: failed to resolve feed channel {ChannelId}",
                _config.ValheimFeedChannelId);
            return null;
        }
    }

    /// <summary>
    /// The world and server names come off the wire from a host we don't control,
    /// and land in a Discord message. Neutralize markdown so they can't forge
    /// formatting or a mass-ping.
    /// </summary>
    public static string Escape(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "the Valheim server"
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
