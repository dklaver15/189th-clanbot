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
    private bool _reportedOffline;

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

            if (_consecutiveFailures == OfflinePollsBeforeClose && _online.Count > 0)
            {
                await CloseAllOpenSessionsAsync(ct);
                _logger.LogInformation(
                    "Palworld server unreachable for {Polls} consecutive polls; closed open sessions at their last-seen time",
                    _consecutiveFailures);
            }

            if (!_reportedOffline && _consecutiveFailures >= OfflinePollsBeforeClose)
            {
                _reportedOffline = true;
                _logger.LogWarning("Palworld server appears offline ({Failures} failed polls)", _consecutiveFailures);
            }

            return;
        }

        if (_consecutiveFailures > 0)
        {
            _logger.LogInformation("Palworld server reachable again after {Failures} failed poll(s)", _consecutiveFailures);
            _consecutiveFailures = 0;
            _reportedOffline = false;
        }

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
            await PostFeedAsync(LeaveMessage(player, played, seen.Count), ct);
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
            await PostFeedAsync(JoinMessage(player, seen.Count), ct);
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

    private static string JoinMessage(PalworldPlayer p, int onlineCount) =>
        $"🟢 **{Escape(p.Name)}** joined the Palworld server" +
        $"{(p.Level > 0 ? $" — level {p.Level}" : "")}  ·  {onlineCount} online";

    private static string LeaveMessage(PalworldPlayer p, TimeSpan? played, int onlineCount) =>
        $"🔴 **{Escape(p.Name)}** left the Palworld server" +
        $"{(played is { TotalMinutes: >= 1 } d ? $" — played {Humanize(d)}" : "")}  ·  {onlineCount} online";

    private async Task PostFeedAsync(string message, CancellationToken ct)
    {
        if (!_config.PalworldFeedEnabled || _config.PalworldFeedChannelId == 0) return;

        try
        {
            var channel = _client.GetChannel(_config.PalworldFeedChannelId) as IMessageChannel
                          ?? await _client.Rest.GetChannelAsync(_config.PalworldFeedChannelId) as IMessageChannel;

            if (channel is null)
            {
                _logger.LogWarning("PalworldPresenceService: could not resolve feed channel {ChannelId}",
                    _config.PalworldFeedChannelId);
                return;
            }

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
