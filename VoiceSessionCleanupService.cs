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
/// Reconciles VoiceSessions against live voice state, at startup and again
/// after every gateway reconnect (Ready), since voice events missed while
/// disconnected are never replayed.
///
/// For each open session (LeftAt IS NULL):
///   • User is in the SAME voice channel now  → leave it (still active).
///   • User is in a DIFFERENT voice channel   → close it (they moved, we missed it).
///   • User is not in any voice channel       → close it (they left while we were down).
/// A user with more than one open session keeps only the newest; older ones
/// close at the newer one's start. Then every member sitting in voice with no
/// open session gets one, so time after a restart counts without them having
/// to rejoin. Sessions in the guild's AFK channel are deleted: AFK time isn't
/// activity, and ActivityTrackingHandler no longer records it.
///
/// A closed session's LeftAt is capped at JoinedAt + MaxSingleSessionHours, and
/// on a reconnect it is also capped at the moment the gateway disconnected, the
/// last time the member was known to be in voice.
/// </summary>
public class VoiceSessionCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<VoiceSessionCleanupService> _logger;
    private readonly BotConfig _config;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private DateTime? _lastDisconnectedUtc;

    public VoiceSessionCleanupService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<VoiceSessionCleanupService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until the Discord client is fully connected and has guild data
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            _logger.LogDebug("VoiceSessionCleanup: waiting for Discord client to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }

        // Give guilds a moment to finish downloading members + voice state
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        await RunSafeAsync(disconnectedAtUtc: null, stoppingToken);

        _client.Disconnected += OnDisconnected;
        _client.Ready += OnReady;
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _client.Disconnected -= OnDisconnected;
            _client.Ready -= OnReady;
        }
    }

    private Task OnDisconnected(Exception _)
    {
        _lastDisconnectedUtc ??= DateTime.UtcNow;
        return Task.CompletedTask;
    }

    private Task OnReady()
    {
        var disconnectedAt = _lastDisconnectedUtc;
        _lastDisconnectedUtc = null;

        // Off the gateway task; wait for voice state to settle after Ready.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            await RunSafeAsync(disconnectedAt, CancellationToken.None);
        });
        return Task.CompletedTask;
    }

    private async Task RunSafeAsync(DateTime? disconnectedAtUtc, CancellationToken ct)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            await RunCleanupAsync(disconnectedAtUtc, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during voice session cleanup");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task RunCleanupAsync(DateTime? disconnectedAtUtc, CancellationToken ct)
    {
        _logger.LogInformation(
            "Voice session cleanup starting (MaxSingleSessionHours={Cap}, disconnectedAt={Disconnected})",
            _config.MaxSingleSessionHours, disconnectedAtUtc?.ToString("u") ?? "n/a");

        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ── AFK channel sessions ──
        var afkDeleted = 0;
        foreach (var guild in _client.Guilds)
        {
            if (guild.AFKChannel is not { } afk) continue;
            afkDeleted += await db.VoiceSessions
                .Where(v => v.GuildId == guild.Id && v.ChannelId == afk.Id)
                .ExecuteDeleteAsync(ct);
        }

        // Last moment a member was known to be in voice: the disconnect, if this
        // run follows a reconnect; otherwise now.
        DateTime CloseAt(VoiceSession s)
        {
            var end = disconnectedAtUtc is { } d && d > s.JoinedAt ? d : now;
            return VoiceActivityHelper.ClampLeftAt(s.JoinedAt, _config.MaxSingleSessionHours, end);
        }

        var openSessions = await db.VoiceSessions
            .Where(v => v.LeftAt == null)
            .ToListAsync(ct);

        var kept = 0;
        var closedMoved = 0;
        var closedOrphaned = 0;
        var closedGuildMissing = 0;
        var closedDuplicate = 0;

        foreach (var userSessions in openSessions.GroupBy(v => (v.GuildId, v.UserId)))
        {
            // Newest first; any older open session was left without us seeing it,
            // so it ends no later than the newer one began.
            var ordered = userSessions.OrderByDescending(v => v.JoinedAt).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var older = ordered[i];
                var nextStart = ordered[i - 1].JoinedAt;
                older.LeftAt = VoiceActivityHelper.ClampLeftAt(older.JoinedAt, _config.MaxSingleSessionHours,
                    nextStart > older.JoinedAt ? nextStart : older.JoinedAt);
                closedDuplicate++;
            }

            var session = ordered[0];
            var guild = _client.GetGuild(session.GuildId);
            if (guild is null)
            {
                // Bot is no longer in that guild — close the session to prevent
                // it from lingering forever in the DB.
                session.LeftAt = CloseAt(session);
                closedGuildMissing++;
                continue;
            }

            var currentChannelId = guild.GetUser(session.UserId)?.VoiceChannel?.Id;

            if (currentChannelId.HasValue && currentChannelId.Value == session.ChannelId)
            {
                kept++;
                continue;
            }

            session.LeftAt = CloseAt(session);
            if (currentChannelId.HasValue)
            {
                closedMoved++;
                _logger.LogInformation(
                    "Cleanup: closed stale session Id={Id} UserId={UserId} — user moved to different channel",
                    session.Id, session.UserId);
            }
            else
            {
                closedOrphaned++;
                _logger.LogInformation(
                    "Cleanup: closed orphan session Id={Id} UserId={UserId} JoinedAt={JoinedAt:yyyy-MM-dd HH:mm} — user not in voice",
                    session.Id, session.UserId, session.JoinedAt);
            }
        }

        // ── Open sessions for members already in voice ──
        var stillOpen = openSessions
            .Where(v => v.LeftAt == null)
            .Select(v => (v.GuildId, v.UserId))
            .ToHashSet();
        var opened = 0;
        foreach (var guild in _client.Guilds)
        {
            var afkId = guild.AFKChannel?.Id;
            foreach (var member in guild.Users)
            {
                if (member.IsBot || member.VoiceChannel is not { } channel || channel.Id == afkId) continue;
                if (stillOpen.Contains((guild.Id, member.Id))) continue;

                db.VoiceSessions.Add(new VoiceSession
                {
                    GuildId = guild.Id,
                    UserId = member.Id,
                    JoinedAt = now,
                    LeftAt = null,
                    ChannelId = channel.Id,
                    ChannelName = channel.Name,
                    CategoryId = channel.CategoryId
                });
                opened++;
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Voice session cleanup complete: kept={Kept}, closed_moved={Moved}, closed_orphaned={Orphans}, " +
            "closed_duplicate={Duplicates}, closed_guild_missing={Missing}, opened={Opened}, afk_deleted={Afk}",
            kept, closedMoved, closedOrphaned, closedDuplicate, closedGuildMissing, opened, afkDeleted);
    }
}
