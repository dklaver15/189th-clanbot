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
/// Derives presence on the clan's Palworld server by polling /players and diffing
/// consecutive snapshots: posts a join/leave feed to
/// <see cref="BotConfig.PalworldFeedChannelId"/> and records every session in
/// <see cref="PalworldSession"/> (which is what /palworld-playtime and the
/// leaderboard read).
///
/// Polling is not a shortcut — the Palworld REST API has no webhooks and no event
/// stream, so diffing snapshots is the only presence mechanism available.
///
/// ── The failure mode this is built around ──
/// A null from <see cref="PalworldApiService.GetPlayersAsync"/> means "unreachable",
/// NOT "empty". Conflating the two would announce a fake mass exodus every time the
/// server restarts, DatHost blips, or the box reboots — with everyone "rejoining"
/// a minute later. So an unreachable poll changes nothing: no leave posts, no
/// session closes, in-memory state untouched.
///
/// Only after <see cref="OfflinePollsBeforeClose"/> consecutive failures do we
/// accept the server is genuinely down, and then sessions are closed SILENTLY at
/// their LastSeenUtc (playtime is preserved and stops at the last poll the player
/// was actually seen) with no leave spam. When the server returns, the players who
/// are on get fresh sessions and normal join posts.
///
/// ── Restart-safe ──
/// Open sessions (EndedUtc = null) are adopted on boot as "currently online". A
/// `docker restart` mid-session therefore neither double-counts the session nor
/// re-announces everyone as joining; and if the player left while the bot was down,
/// the first poll closes their session at LastSeenUtc — crediting only the time
/// they were actually observed, never the downtime.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.PalworldEnabled"/> is true and the API client
/// is configured. The feed is separately gated by
/// <see cref="BotConfig.PalworldFeedEnabled"/> + a channel id, so session/playtime
/// tracking can run with the chat feed turned off.
/// </summary>
public sealed class PalworldPresenceService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Consecutive unreachable polls before we conclude the server is really down
    /// and close the open sessions. 5 × the poll interval (default 60s) ≈ 5 minutes,
    /// which comfortably rides out a DatHost restart or an update without touching
    /// anyone's session.
    /// </summary>
    private const int OfflinePollsBeforeClose = 5;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly PalworldApiService _api;
    private readonly BotConfig _config;
    private readonly ILogger<PalworldPresenceService> _logger;

    /// <summary>
    /// Who we believe is online, keyed by the stable identity. Mirrors the open
    /// PalworldSession rows; rebuilt from the DB on boot.
    /// </summary>
    private readonly Dictionary<string, PalworldPlayer> _online = new(StringComparer.Ordinal);

    private int _consecutiveFailures;

    /// <summary>
    /// What we've last ANNOUNCED about the whole server's reachability.
    /// null  = unknown (haven't reached a verdict yet, e.g. just booted).
    /// true  = we believe it's up.
    /// false = we've announced it's down.
    ///
    /// The null start is what stops a "server is online!" post every time the bot
    /// restarts while the server was already up: we only announce online on a
    /// false→true transition (a real recovery), never null→true (first contact).
    /// Likewise offline only fires true→false, never null→false, so booting the bot
    /// while the server happens to be down stays quiet.
    /// </summary>
    private bool? _serverOnline;

    /// <summary>Consecutive samples at or below the FPS threshold. Reset by any healthy sample.</summary>
    private int _consecutiveLowFps;

    /// <summary>True once a lag alert has been posted, until performance recovers.</summary>
    private bool _inLagState;

    private DateTime _lastLagAlertUtc = DateTime.MinValue;
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public PalworldPresenceService(
        IServiceProvider services,
        DiscordSocketClient client,
        PalworldApiService api,
        IOptions<BotConfig> config,
        ILogger<PalworldPresenceService> logger)
    {
        _services = services;
        _client = client;
        _api = api;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.PalworldPollIntervalSeconds, 15, 3600));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the gateway so the feed channel resolves.
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!_config.PalworldEnabled || !_api.IsConfigured)
        {
            _logger.LogInformation(
                "PalworldPresenceService idle (enabled={Enabled}, configured={Configured})",
                _config.PalworldEnabled, _api.IsConfigured);
            return;
        }

        await AdoptOpenSessionsAsync(stoppingToken);

        _logger.LogInformation(
            "PalworldPresenceService started; polling every {Seconds}s, feed={Feed} (channel {ChannelId}), adopted {Open} open session(s)",
            (int)PollInterval.TotalSeconds, _config.PalworldFeedEnabled,
            _config.PalworldFeedChannelId, _online.Count);

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
                _logger.LogError(ex, "PalworldPresenceService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Rebuilds the in-memory online set from the open sessions left behind by the
    /// previous run, so a restart doesn't re-announce everyone as joining. The rows
    /// are trusted as-is; the first poll immediately corrects them (anyone who
    /// actually left gets their session closed at LastSeenUtc).
    /// </summary>
    private async Task AdoptOpenSessionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var open = await db.PalworldSessions
            .Where(s => s.EndedUtc == null)
            .ToListAsync(ct);

        foreach (var s in open)
        {
            var key = KeyFor(s.PalworldUserId, s.PalworldPlayerId);
            if (key.Length == 0) continue;

            _online[key] = new PalworldPlayer(
                Name: s.PlayerName,
                AccountName: s.AccountName,
                PlayerId: s.PalworldPlayerId,
                UserId: s.PalworldUserId,
                Ping: 0,
                Level: s.Level,
                BuildingCount: s.BuildingCount);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var players = await _api.GetPlayersAsync(ct);

        // ── Unreachable ──
        // null is "unknown", never "empty". Hold state and wait it out.
        if (players is null)
        {
            _consecutiveFailures++;

            // The "== threshold" (not ">=") makes this fire exactly once, on the
            // poll that crosses the line — everything past that point is already
            // handled. A brief DatHost reboot that recovers in under
            // OfflinePollsBeforeClose polls never reaches here, so it produces no
            // offline announcement AND no matching "online" post on recovery: short
            // blips stay silent, which is the point of the delay.
            if (_consecutiveFailures == OfflinePollsBeforeClose)
            {
                if (_online.Count > 0)
                {
                    await CloseAllOpenSessionsAsync(ct);
                    _logger.LogInformation(
                        "Palworld server unreachable for {Polls} consecutive polls; closed open sessions at their last-seen time",
                        _consecutiveFailures);
                }

                _logger.LogWarning("Palworld server appears offline ({Failures} failed polls)", _consecutiveFailures);

                // Announce only if we'd previously confirmed it UP. A null verdict
                // (bot booted into an already-down server) flips to false quietly.
                if (_serverOnline == true)
                    await PostServerStatusAsync(online: false, ct);
                _serverOnline = false;
            }

            return;
        }

        if (_consecutiveFailures > 0)
        {
            _logger.LogInformation("Palworld server reachable again after {Failures} failed poll(s)", _consecutiveFailures);
            _consecutiveFailures = 0;
        }

        // Announce recovery only on a real false→true flip. First contact (null)
        // just records the state — the server didn't "come up", it was already up.
        if (_serverOnline == false)
            await PostServerStatusAsync(online: true, ct);
        _serverOnline = true;

        // Health sampling rides on this same tick rather than running its own poll
        // loop — one extra request a minute, and it reuses the reachability verdict
        // above so we never record a sample for an unreachable server.
        await SampleHealthAsync(ct);

        var seen = new Dictionary<string, PalworldPlayer>(StringComparer.Ordinal);
        foreach (var p in players)
        {
            var key = KeyFor(p.UserId, p.PlayerId);
            if (key.Length == 0) continue;
            seen[key] = p;
        }

        var joined = seen.Where(kv => !_online.ContainsKey(kv.Key)).ToList();
        var left = _online.Where(kv => !seen.ContainsKey(kv.Key)).ToList();

        if (joined.Count == 0 && left.Count == 0)
        {
            // Steady state: still refresh the open rows so LastSeenUtc/level track
            // reality. This is what makes an open session's duration trustworthy.
            if (seen.Count > 0) await TouchOpenSessionsAsync(seen, ct);
            return;
        }

        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Canonical-name overrides for whoever joined or left this tick, so the feed
        // shows a member's pinned clan gamertag rather than whatever identity the
        // server reported this session (Steam name vs Game Center name). Sessions
        // still STORE the raw reported name; only the feed label is corrected.
        var affectedIds = joined.Select(kv => kv.Value.UserId)
            .Concat(left.Select(kv => kv.Value.UserId))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
        var overrides = affectedIds.Count == 0
            ? new Dictionary<string, string>()
            : await db.PalworldNameOverrides
                .Where(o => affectedIds.Contains(o.PalworldUserId))
                .ToDictionaryAsync(o => o.PalworldUserId, o => o.CanonicalName, ct);

        // ── Leaves ──
        foreach (var (key, player) in left)
        {
            var session = await db.PalworldSessions
                .Where(s => s.EndedUtc == null && s.PalworldUserId == player.UserId)
                .OrderByDescending(s => s.StartedUtc)
                .FirstOrDefaultAsync(ct);

            TimeSpan? played = null;
            if (session is not null)
            {
                session.EndedUtc = now;
                session.LastSeenUtc = now;
                played = session.Duration;
            }

            _online.Remove(key);
            await PostFeedAsync(LeaveMessage(FeedName(player, overrides), played, seen.Count), ct);
        }

        // ── Joins ──
        foreach (var (key, player) in joined)
        {
            db.PalworldSessions.Add(new PalworldSession
            {
                PalworldUserId = player.UserId,
                PalworldPlayerId = player.PlayerId,
                PlayerName = player.Name,
                AccountName = player.AccountName,
                StartedUtc = now,
                LastSeenUtc = now,
                EndedUtc = null,
                Level = player.Level,
                BuildingCount = player.BuildingCount,
            });

            _online[key] = player;
            await PostFeedAsync(JoinMessage(FeedName(player, overrides), player.Level, seen.Count), ct);
        }

        await db.SaveChangesAsync(ct);

        // Refresh the survivors' rows in the same pass.
        if (seen.Count > 0) await TouchOpenSessionsAsync(seen, ct);
    }

    /// <summary>
    /// Bumps LastSeenUtc (and the level/building high-water marks) on every open
    /// session for a player we can still see. Level uses Math.Max so a session's
    /// recorded level is the best reached, not whatever happened to be true at the
    /// final poll.
    /// </summary>
    private async Task TouchOpenSessionsAsync(Dictionary<string, PalworldPlayer> seen, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var open = await db.PalworldSessions
            .Where(s => s.EndedUtc == null)
            .ToListAsync(ct);
        if (open.Count == 0) return;

        var now = DateTime.UtcNow;
        var changed = false;

        foreach (var s in open)
        {
            var key = KeyFor(s.PalworldUserId, s.PalworldPlayerId);
            if (!seen.TryGetValue(key, out var p)) continue;

            s.LastSeenUtc = now;
            s.Level = Math.Max(s.Level, p.Level);
            s.BuildingCount = p.BuildingCount;
            if (!string.IsNullOrWhiteSpace(p.Name)) s.PlayerName = p.Name;
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Closes every open session at its own LastSeenUtc — used when the server has
    /// been unreachable long enough to be considered down. Deliberately silent: the
    /// players didn't "leave", we lost visibility, and a burst of leave posts for a
    /// server restart is exactly the noise this feature must not produce.
    /// </summary>
    private async Task CloseAllOpenSessionsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var open = await db.PalworldSessions
            .Where(s => s.EndedUtc == null)
            .ToListAsync(ct);

        foreach (var s in open)
            s.EndedUtc = s.LastSeenUtc;   // credit only what we actually observed

        if (open.Count > 0) await db.SaveChangesAsync(ct);

        _online.Clear();
    }

    // ─── Health sampling + lag alerting ──────────────────────────────────────

    /// <summary>
    /// Records one <see cref="PalworldMetricSample"/> and evaluates the lag alert.
    /// Called only on a reachable tick.
    ///
    /// Entirely best-effort: any failure here is logged and swallowed, because
    /// performance telemetry must never be able to disrupt presence tracking, which
    /// is the feature people actually notice.
    /// </summary>
    private async Task SampleHealthAsync(CancellationToken ct)
    {
        if (!_config.PalworldMetricsSamplingEnabled) return;

        try
        {
            var m = await _api.GetMetricsAsync(ct);
            if (m is null) return;   // transient; the next tick will try again

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var now = DateTime.UtcNow;

            db.PalworldMetricSamples.Add(new PalworldMetricSample
            {
                SampledUtc = now,
                ServerFps = m.ServerFps,
                FrameTimeMs = m.ServerFrameTime,
                PlayerCount = m.CurrentPlayerNum,
                MaxPlayerCount = m.MaxPlayerNum,
                UptimeSeconds = m.Uptime,
                BaseCampCount = m.BaseCampNum,
                InGameDay = m.Days,
            });

            await db.SaveChangesAsync(ct);

            await EvaluateLagAlertAsync(m, ct);
            await PruneOldSamplesAsync(db, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PalworldPresenceService: health sampling failed");
        }
    }

    /// <summary>
    /// Posts a lag alert once FPS has stayed at/below the threshold for
    /// <see cref="BotConfig.PalworldLagAlertConsecutiveSamples"/> consecutive
    /// samples, and a recovery notice when it climbs back.
    ///
    /// ── Why the debounce and the cooldown ──
    /// A single low sample is noise: a world save, an autosave, or a raid spawning
    /// can dip FPS for one tick. Requiring sustained lows means the alert tracks
    /// something players are actually feeling. The cooldown then stops an evening
    /// spent hovering near the threshold from becoming a stream of near-identical
    /// posts.
    ///
    /// Samples with nobody online are ignored: an idle server dipping isn't
    /// actionable, and alerting on it would fire overnight when no one cares.
    /// </summary>
    private async Task EvaluateLagAlertAsync(PalworldMetrics m, CancellationToken ct)
    {
        if (!_config.PalworldLagAlertEnabled) return;

        var threshold = Math.Max(1, _config.PalworldLagAlertFpsThreshold);
        var needed = Math.Max(1, _config.PalworldLagAlertConsecutiveSamples);

        var struggling = m.ServerFps <= threshold && m.CurrentPlayerNum > 0;

        if (!struggling)
        {
            // Recovered: only worth announcing if we'd actually alerted.
            if (_inLagState && m.ServerFps > threshold)
            {
                _inLagState = false;
                _consecutiveLowFps = 0;
                await PostLagRecoveredAsync(m, ct);
            }
            else
            {
                _consecutiveLowFps = 0;
            }
            return;
        }

        _consecutiveLowFps++;

        if (_inLagState) return;                        // already reported this episode
        if (_consecutiveLowFps < needed) return;        // not yet sustained

        var cooldown = TimeSpan.FromMinutes(Math.Max(0, _config.PalworldLagAlertCooldownMinutes));
        if (DateTime.UtcNow - _lastLagAlertUtc < cooldown)
        {
            // Still inside the quiet period. Mark the episode as reported anyway so
            // we don't fire the instant the cooldown lapses on the same slump.
            _inLagState = true;
            return;
        }

        _inLagState = true;
        _lastLagAlertUtc = DateTime.UtcNow;
        await PostLagAlertAsync(m, ct);
    }

    private async Task PostLagAlertAsync(PalworldMetrics m, CancellationToken ct)
    {
        var embed = new EmbedBuilder()
            .WithColor(new Color(0xE67E22))   // amber: degraded, not down
            .WithTitle("⚠️ Palworld server is struggling")
            .WithDescription(
                $"Server FPS has been at or below **{_config.PalworldLagAlertFpsThreshold}** for " +
                $"{_consecutiveLowFps} straight checks. Expect rubber-banding and delayed actions.")
            .AddField("Server FPS", m.ServerFps.ToString(), true)
            .AddField("Players", $"{m.CurrentPlayerNum}/{m.MaxPlayerNum}", true)
            .AddField("Up for", Humanize(TimeSpan.FromSeconds(m.Uptime)), true)
            .WithFooter("Run /palworld-performance to see whether this tracks player count or uptime.")
            .WithCurrentTimestamp();

        await PostEmbedAsync(embed.Build(), "lag alert", ct);

        _logger.LogWarning(
            "Palworld lag alert: {Fps} FPS with {Players} players, uptime {Uptime}s",
            m.ServerFps, m.CurrentPlayerNum, m.Uptime);
    }

    private async Task PostLagRecoveredAsync(PalworldMetrics m, CancellationToken ct)
    {
        var embed = new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle("✅ Palworld server performance recovered")
            .WithDescription($"Server FPS is back up to **{m.ServerFps}**.")
            .AddField("Players", $"{m.CurrentPlayerNum}/{m.MaxPlayerNum}", true)
            .AddField("Up for", Humanize(TimeSpan.FromSeconds(m.Uptime)), true)
            .WithCurrentTimestamp();

        await PostEmbedAsync(embed.Build(), "lag recovery", ct);

        _logger.LogInformation("Palworld performance recovered: {Fps} FPS", m.ServerFps);
    }

    /// <summary>
    /// Drops samples past the retention window. Runs at most every 6 hours rather
    /// than on every tick — the table is small and the delete is pure housekeeping,
    /// so there's no reason to touch it 1,440 times a day.
    /// </summary>
    private async Task PruneOldSamplesAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        if (_config.PalworldMetricsRetentionDays <= 0) return;
        if (now - _lastPruneUtc < TimeSpan.FromHours(6)) return;

        _lastPruneUtc = now;

        var cutoff = now.AddDays(-_config.PalworldMetricsRetentionDays);
        var stale = await db.PalworldMetricSamples
            .Where(s => s.SampledUtc < cutoff)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        db.PalworldMetricSamples.RemoveRange(stale);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Pruned {Count} Palworld health sample(s) older than {Days}d",
            stale.Count, _config.PalworldMetricsRetentionDays);
    }

    private static string JoinMessage(string displayName, int level, int onlineCount) =>
        $"🟢 **{Escape(displayName)}** joined the Palworld server" +
        $"{(level > 0 ? $" — level **{level}**" : "")}  ·  {onlineCount} online";

    private static string LeaveMessage(string displayName, TimeSpan? played, int onlineCount) =>
        $"🔴 **{Escape(displayName)}** left the Palworld server" +
        $"{(played is { TotalMinutes: >= 1 } d ? $" — played {Humanize(d)}" : "")}  ·  {onlineCount} online";

    /// <summary>
    /// The name to show in the feed: the canonical override for this player's
    /// PalworldUserId when one is set, otherwise the raw in-game name the server
    /// reported. Single point of resolution shared by joins and leaves.
    /// </summary>
    private static string FeedName(PalworldPlayer p, IReadOnlyDictionary<string, string> overrides) =>
        !string.IsNullOrEmpty(p.UserId)
        && overrides.TryGetValue(p.UserId, out var canonical)
        && !string.IsNullOrWhiteSpace(canonical)
            ? canonical
            : p.Name;

    private async Task PostFeedAsync(string message, CancellationToken ct)
    {
        if (!_config.PalworldFeedEnabled || _config.PalworldFeedChannelId == 0) return;

        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            await channel.SendMessageAsync(
                text: message,
                allowedMentions: AllowedMentions.None,   // in-game names must never ping anyone
                options: new RequestOptions { CancelToken = cts.Token });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed feed post must never cost us the session write.
            _logger.LogWarning(ex, "PalworldPresenceService: failed to post feed message");
        }
    }

    /// <summary>
    /// Posts the whole-server up/down notice as a green/red embed — a rare, one-off
    /// event (maybe once or twice a day, gated behind the same offline delay as the
    /// session logic), which is exactly the kind of thing an embed suits, unlike the
    /// high-volume join/leave lines that stay plain text on purpose.
    ///
    /// Shares the feed channel, and is gated by BOTH the feed switch (a channel is
    /// needed either way) and its own PalworldServerStatusAnnounceEnabled, so the
    /// up/down pings can be silenced without losing the join/leave feed. Best-effort
    /// — a failed post is logged and dropped, never propagated into the poll loop.
    /// </summary>
    private async Task PostServerStatusAsync(bool online, CancellationToken ct)
    {
        if (!_config.PalworldServerStatusAnnounceEnabled) return;
        if (!_config.PalworldFeedEnabled || _config.PalworldFeedChannelId == 0) return;

        // Enrich the "online" post with the server name when we can get it; purely
        // cosmetic, so a failure here just omits the footer.
        string? serverName = null;
        if (online)
        {
            try { serverName = (await _api.GetInfoAsync(ct))?.ServerName; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* cosmetic only */ }
        }

        var embed = new EmbedBuilder()
            .WithColor(online ? Color.Green : Color.Red)
            .WithTitle(online ? "🟢 Palworld server is online" : "🔴 Palworld server went offline")
            .WithDescription(online
                ? "The clan's Palworld server is up — hop on!"
                : "The clan's Palworld server stopped responding. It may be restarting or down.")
            .WithCurrentTimestamp();

        if (!string.IsNullOrWhiteSpace(serverName))
            embed.WithFooter(serverName);

        await PostEmbedAsync(embed.Build(), $"server {(online ? "online" : "offline")} announcement", ct);

        _logger.LogInformation("Posted Palworld server {State} announcement", online ? "online" : "offline");
    }

    /// <summary>
    /// Sends an embed to the feed channel. Best-effort: resolution and send failures
    /// are logged and swallowed, never propagated into the poll loop.
    /// <paramref name="what"/> is used only for the failure log line.
    /// </summary>
    private async Task PostEmbedAsync(Embed embed, string what, CancellationToken ct)
    {
        if (!_config.PalworldFeedEnabled || _config.PalworldFeedChannelId == 0) return;

        var channel = await ResolveFeedChannelAsync(ct);
        if (channel is null) return;

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
            _logger.LogWarning(ex, "PalworldPresenceService: failed to post {What}", what);
        }
    }

    /// <summary>
    /// Resolves the feed channel from the gateway cache, falling back to a REST
    /// lookup so an uncached channel still resolves. Null (logged) when it can't be
    /// found — callers treat that as "skip the post".
    /// </summary>
    private async Task<IMessageChannel?> ResolveFeedChannelAsync(CancellationToken ct)
    {
        var channel = _client.GetChannel(_config.PalworldFeedChannelId) as IMessageChannel
                      ?? await _client.Rest.GetChannelAsync(_config.PalworldFeedChannelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

        if (channel is null)
            _logger.LogWarning("PalworldPresenceService: could not resolve feed channel {ChannelId}",
                _config.PalworldFeedChannelId);

        return channel;
    }

    /// <summary>
    /// The identity we diff on. UserId is the stable platform id and is preferred;
    /// PlayerId is only a fallback for the degenerate case where the API omits it.
    /// Must match <see cref="PalworldSession"/>'s stored ids or a restart would
    /// re-announce everyone.
    /// </summary>
    internal static string KeyFor(string userId, string playerId) =>
        !string.IsNullOrWhiteSpace(userId) ? $"u:{userId}"
        : !string.IsNullOrWhiteSpace(playerId) ? $"p:{playerId}"
        : "";

    /// <summary>"2h 14m" / "47m". Shared with the playtime command's formatting.</summary>
    internal static string Humanize(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return "under a minute";
        var hours = (int)d.TotalHours;
        var minutes = d.Minutes;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    /// <summary>
    /// In-game names are attacker-controlled text landing in a Discord message.
    /// Neutralize markdown so nobody can name themselves into a fake embed, a
    /// mass-ping, or a link. AllowedMentions.None already blocks the ping; this
    /// blocks the formatting.
    /// </summary>
    private static string Escape(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "(unnamed)"
            : name.Replace("\\", "\\\\")
                  .Replace("*", "\\*")
                  .Replace("_", "\\_")
                  .Replace("~", "\\~")
                  .Replace("`", "\\`")
                  .Replace("|", "\\|")
                  .Replace("@", "@​")
                  .Replace("\n", " ")
                  .Replace("\r", " ");
}
