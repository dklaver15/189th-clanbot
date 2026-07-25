using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Two jobs against the clan's Satisfactory factory, both powered by the Ficsit
/// Remote Monitoring mod:
///
///   • <b>Alerts</b> — a fast poll for things somebody needs to know about NOW:
///     a tripped fuse, or a battery bank running down. Posted on the transition
///     into the bad state, and again when it clears.
///   • <b>Digest</b> — a once-a-day factory report: session age, power, AWESOME
///     Sink progress, space-elevator phase, and who's been playing.
///
/// ── Why one service and not two ──
/// They share a client, a channel resolver and an embed style, and the digest is
/// a once-a-day branch inside a loop that has to run anyway. Splitting them would
/// double the boilerplate to save nothing.
///
/// ── Quiet on boot ──
/// The FIRST alert check records state without announcing anything. Otherwise
/// every redeploy would re-announce a fuse that has been tripped for hours, which
/// trains people to ignore the alerts.
///
/// ── Cost ──
/// FRM's web server runs off the game thread, so these reads are cheap — but they
/// are not free, and this deliberately polls only the small endpoints. getFactory
/// and getAll walk the whole save and must never go in a loop.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.SatisfactoryEnabled"/> and FRM are both
/// configured. Alerts and the digest are then independently switchable.
/// </summary>
public sealed class SatisfactoryFactoryService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    /// <summary>Hysteresis on the battery alert, in percentage points.</summary>
    private const double BatteryRecoveryMargin = 10;

    private readonly DiscordSocketClient _client;
    private readonly FrmApiService _frm;
    private readonly SatisfactoryDigestBuilder _digest;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryFactoryService> _logger;

    /// <summary>Circuits currently in the tripped state, as far as we've announced.</summary>
    private readonly HashSet<int> _fuseAlerted = new();

    /// <summary>Circuits currently in the low-battery state, as far as we've announced.</summary>
    private readonly HashSet<int> _batteryAlerted = new();

    /// <summary>Last time we posted about a circuit, for flap throttling.</summary>
    private readonly Dictionary<int, DateTime> _lastCircuitAlertUtc = new();

    /// <summary>False until the first successful alert read — see "quiet on boot".</summary>
    private bool _alertStateSeeded;

    /// <summary>UTC date of the last digest posted, so it fires once per day.</summary>
    private DateTime? _lastDigestDateUtc;

    public SatisfactoryFactoryService(
        DiscordSocketClient client,
        FrmApiService frm,
        SatisfactoryDigestBuilder digest,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryFactoryService> logger)
    {
        _client = client;
        _frm = frm;
        _digest = digest;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryAlertIntervalSeconds, 30, 3600));

    private TimeSpan AlertCooldown =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryAlertCooldownMinutes, 1, 1440));

    private bool Active => _config.SatisfactoryEnabled && _config.FrmEnabled && _frm.IsConfigured;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!Active)
        {
            _logger.LogInformation(
                "SatisfactoryFactoryService idle (satisfactory={Sat}, frm={Frm})",
                _config.SatisfactoryEnabled, _config.FrmEnabled && _frm.IsConfigured);
            return;
        }

        // If we've booted AFTER today's digest slot, mark today as done so a
        // redeploy in the evening doesn't post a second one. Booting before the
        // slot leaves it null, so today's digest still fires on schedule.
        if (DateTime.UtcNow.Hour >= DigestHourUtc())
            _lastDigestDateUtc = DateTime.UtcNow.Date;

        _logger.LogInformation(
            "SatisfactoryFactoryService started; polling every {Seconds}s, alerts={Alerts}, digest={Digest} (hour {Hour} UTC)",
            (int)PollInterval.TotalSeconds, _config.SatisfactoryAlertsEnabled,
            _config.SatisfactoryDigestEnabled, DigestHourUtc());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.SatisfactoryAlertsEnabled) await CheckAlertsAsync(stoppingToken);
                if (_config.SatisfactoryDigestEnabled) await MaybePostDigestAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SatisfactoryFactoryService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private int DigestHourUtc() => Math.Clamp(_config.SatisfactoryDigestHourUtc, 0, 23);

    // ─── Alerts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls getPower and announces transitions into and out of the two states
    /// worth waking someone for: a tripped fuse, and a battery bank draining
    /// toward empty.
    /// </summary>
    private async Task CheckAlertsAsync(CancellationToken ct)
    {
        var circuits = await _frm.GetPowerAsync(ct);

        // null is "unknown" — the server or FRM is unreachable. Hold all state:
        // clearing it here would replay every active alert on recovery.
        if (circuits is null) return;

        var now = DateTime.UtcNow;

        foreach (var c in circuits)
        {
            await EvaluateFuseAsync(c, now, ct);
            await EvaluateBatteryAsync(c, now, ct);
        }

        // Circuits can disappear (rebuilt, merged, dismantled). Drop their state
        // so a returning circuit id doesn't inherit a stale "already alerted".
        var live = circuits.Select(c => c.CircuitGroupId).ToHashSet();
        _fuseAlerted.RemoveWhere(id => !live.Contains(id));
        _batteryAlerted.RemoveWhere(id => !live.Contains(id));

        // The first successful read only seeds state — see "quiet on boot".
        if (!_alertStateSeeded)
        {
            _alertStateSeeded = true;
            foreach (var c in circuits)
            {
                if (c.FuseTriggered) _fuseAlerted.Add(c.CircuitGroupId);
                if (IsBatteryLow(c)) _batteryAlerted.Add(c.CircuitGroupId);
            }

            if (_fuseAlerted.Count > 0 || _batteryAlerted.Count > 0)
                _logger.LogInformation(
                    "Satisfactory alerts seeded on boot: {Fuses} tripped fuse(s), {Batteries} low batter(ies) — not announced",
                    _fuseAlerted.Count, _batteryAlerted.Count);
        }
    }

    private async Task EvaluateFuseAsync(FrmPowerCircuit c, DateTime now, CancellationToken ct)
    {
        if (!_alertStateSeeded) return;   // seeding pass handles this tick

        var wasAlerted = _fuseAlerted.Contains(c.CircuitGroupId);

        if (c.FuseTriggered && !wasAlerted)
        {
            if (!PassesCooldown(c.CircuitGroupId, now)) return;
            _fuseAlerted.Add(c.CircuitGroupId);

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("⚡ Fuse tripped")
                .WithDescription(
                    $"Circuit **{c.CircuitGroupId}** has blown its fuse — everything on it is offline until someone resets it.")
                .AddField("Draw before it blew", $"{c.PowerMaxConsumed:0.#} MW", true)
                .AddField("Capacity", $"{c.PowerCapacity:0.#} MW", true)
                .WithCurrentTimestamp()
                .Build(), ct, "fuse alert");
        }
        else if (!c.FuseTriggered && wasAlerted)
        {
            _fuseAlerted.Remove(c.CircuitGroupId);

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("⚡ Fuse reset")
                .WithDescription($"Circuit **{c.CircuitGroupId}** is back online.")
                .WithCurrentTimestamp()
                .Build(), ct, "fuse recovery");
        }
    }

    private async Task EvaluateBatteryAsync(FrmPowerCircuit c, DateTime now, CancellationToken ct)
    {
        if (!_alertStateSeeded) return;

        var wasAlerted = _batteryAlerted.Contains(c.CircuitGroupId);
        var low = IsBatteryLow(c);

        if (low && !wasAlerted)
        {
            if (!PassesCooldown(c.CircuitGroupId, now)) return;
            _batteryAlerted.Add(c.CircuitGroupId);

            // BatteryTimeEmpty is an "HH:MM:SS" string from FRM, not a number.
            var eta = string.IsNullOrWhiteSpace(c.BatteryTimeEmpty) || c.BatteryTimeEmpty == "00:00:00"
                ? "unknown"
                : c.BatteryTimeEmpty;

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(new Color(0xF1C40F))
                .WithTitle("🔋 Batteries running down")
                .WithDescription(
                    $"Circuit **{c.CircuitGroupId}** is drawing more than it makes and the batteries are covering the gap.")
                .AddField("Charge left", $"{c.BatteryPercent:0.#}%", true)
                .AddField("Time to empty", eta, true)
                .AddField("Draw vs production", $"{c.PowerConsumed:0.#} / {c.PowerProduction:0.#} MW", true)
                .WithCurrentTimestamp()
                .Build(), ct, "battery alert");
        }
        else if (wasAlerted && c.BatteryPercent > _config.SatisfactoryBatteryAlertPercent + BatteryRecoveryMargin)
        {
            // Hysteresis: recover well above the trigger, so a bank hovering at
            // the threshold doesn't alternate alert/recovery every poll.
            _batteryAlerted.Remove(c.CircuitGroupId);

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("🔋 Batteries recovering")
                .WithDescription($"Circuit **{c.CircuitGroupId}** is charging again — {c.BatteryPercent:0.#}%.")
                .WithCurrentTimestamp()
                .Build(), ct, "battery recovery");
        }
    }

    /// <summary>
    /// A circuit counts as low only if it actually HAS batteries. Without the
    /// capacity check every circuit reads as 0% and would alert forever — which
    /// is the clan's current state, since nobody has built a battery yet.
    /// </summary>
    private bool IsBatteryLow(FrmPowerCircuit c) =>
        c.BatteryCapacity > 0 && c.BatteryPercent <= _config.SatisfactoryBatteryAlertPercent;

    /// <summary>
    /// Throttles repeat alerts for one circuit. Recoveries deliberately bypass
    /// this — "it's fixed" should never be suppressed.
    /// </summary>
    private bool PassesCooldown(int circuitId, DateTime now)
    {
        if (_lastCircuitAlertUtc.TryGetValue(circuitId, out var last) && now - last < AlertCooldown)
            return false;

        _lastCircuitAlertUtc[circuitId] = now;
        return true;
    }

    // ─── Daily digest ────────────────────────────────────────────────────────

    private async Task MaybePostDigestAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now.Hour != DigestHourUtc()) return;
        if (_lastDigestDateUtc == now.Date) return;

        _lastDigestDateUtc = now.Date;
        await PostDigestAsync(ct);
    }

    private async Task PostDigestAsync(CancellationToken ct)
    {
        var embed = await _digest.BuildAsync(ct);
        if (embed is null)
        {
            _logger.LogInformation("Satisfactory digest skipped — server unreachable");
            return;
        }

        await PostDigestEmbedAsync(embed, ct);
    }

    // ─── Posting ─────────────────────────────────────────────────────────────

    /// <summary>Alerts go to the presence feed channel — same audience, same urgency band.</summary>
    private Task PostAlertAsync(Embed embed, CancellationToken ct, string what) =>
        PostToAsync(_config.SatisfactoryFeedChannelId, embed, ct, what);

    /// <summary>
    /// The digest goes to its own channel when one is set, else the feed channel.
    /// A daily report and a join/leave feed often want different homes.
    /// </summary>
    private Task PostDigestEmbedAsync(Embed embed, CancellationToken ct) =>
        PostToAsync(
            _config.SatisfactoryDigestChannelId != 0
                ? _config.SatisfactoryDigestChannelId
                : _config.SatisfactoryFeedChannelId,
            embed, ct, "digest");

    private async Task PostToAsync(ulong channelId, Embed embed, CancellationToken ct, string what)
    {
        if (channelId == 0) return;

        var channel = _client.GetChannel(channelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(channelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

        if (channel is null)
        {
            _logger.LogWarning("SatisfactoryFactoryService: could not resolve channel {ChannelId} for {What}", channelId, what);
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await channel.SendMessageAsync(
                embed: embed,
                allowedMentions: AllowedMentions.None,
                options: new RequestOptions { CancelToken = cts.Token });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryFactoryService: failed to post {What}", what);
        }
    }

    /// <summary>
    /// Session and item names are builder-controlled text landing in a Discord
    /// message. Neutralize markdown so they can't forge formatting or a mass-ping.
    /// </summary>
    private static string Escape(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "(unnamed)"
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
