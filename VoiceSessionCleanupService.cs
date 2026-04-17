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
/// Runs once at bot startup to reconcile stuck voice sessions — rows in
/// VoiceSessions with LeftAt IS NULL that do not correspond to a member who
/// is actually in voice right now. Such orphans are left behind when the bot
/// crashes or loses gateway connection while users are in voice channels.
///
/// Reconciliation logic for each open session:
///   • User is in the SAME voice channel now  → leave it (still active).
///   • User is in a DIFFERENT voice channel   → close it (they moved, we missed it).
///   • User is not in any voice channel       → close it (they left while we were down).
///
/// Closed sessions get LeftAt = min(JoinedAt + MaxSingleSessionHours, now), so a
/// very old stuck session gets credited at most the configured cap. This keeps
/// behavior consistent with the per-session cap applied by VoiceActivityHelper.
/// </summary>
public class VoiceSessionCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<VoiceSessionCleanupService> _logger;
    private readonly BotConfig _config;

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

        try
        {
            await RunCleanupAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during voice session cleanup");
        }

        // One-shot: service exits after a single cleanup pass. Future stuck sessions
        // that accumulate during runtime are bounded by the per-session cap applied
        // in VoiceActivityHelper, and will be cleaned up on the next bot startup.
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        _logger.LogInformation("Voice session cleanup starting (MaxSingleSessionHours={Cap})",
            _config.MaxSingleSessionHours);

        var cap = TimeSpan.FromHours(_config.MaxSingleSessionHours);
        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var openSessions = await db.VoiceSessions
            .Where(v => v.LeftAt == null)
            .ToListAsync(ct);

        if (openSessions.Count == 0)
        {
            _logger.LogInformation("Voice session cleanup: no open sessions to reconcile");
            return;
        }

        _logger.LogInformation("Voice session cleanup: found {Count} open session(s) to reconcile",
            openSessions.Count);

        var kept = 0;
        var closedMoved = 0;
        var closedOrphaned = 0;
        var closedGuildMissing = 0;

        foreach (var session in openSessions)
        {
            var guild = _client.GetGuild(session.GuildId);
            if (guild is null)
            {
                // Bot is no longer in that guild — close the session to prevent
                // it from lingering forever in the DB.
                session.LeftAt = ClampLeftAt(session.JoinedAt, cap, now);
                closedGuildMissing++;
                _logger.LogInformation(
                    "Cleanup: closed session Id={Id} UserId={UserId} (guild {GuildId} not present)",
                    session.Id, session.UserId, session.GuildId);
                continue;
            }

            var member = guild.GetUser(session.UserId);
            var currentChannelId = member?.VoiceChannel?.Id;

            if (currentChannelId.HasValue && currentChannelId.Value == session.ChannelId)
            {
                // Still in the same channel — session is genuinely active. Leave it.
                kept++;
                _logger.LogDebug(
                    "Cleanup: keeping active session Id={Id} UserId={UserId} Channel={Channel}",
                    session.Id, session.UserId, session.ChannelName);
                continue;
            }

            if (currentChannelId.HasValue)
            {
                // User is in voice but in a different channel — close this stale session.
                // ActivityTrackingHandler should have already opened a fresh session for
                // the current channel; if not, it will the next time the gateway updates.
                session.LeftAt = ClampLeftAt(session.JoinedAt, cap, now);
                closedMoved++;
                _logger.LogInformation(
                    "Cleanup: closed stale session Id={Id} UserId={UserId} — user moved to different channel",
                    session.Id, session.UserId);
            }
            else
            {
                // User isn't in voice at all. Close the orphan.
                session.LeftAt = ClampLeftAt(session.JoinedAt, cap, now);
                closedOrphaned++;
                _logger.LogInformation(
                    "Cleanup: closed orphan session Id={Id} UserId={UserId} JoinedAt={JoinedAt:yyyy-MM-dd HH:mm} — user not in voice",
                    session.Id, session.UserId, session.JoinedAt);
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Voice session cleanup complete: kept={Kept}, closed_moved={Moved}, closed_orphaned={Orphans}, closed_guild_missing={Missing}",
            kept, closedMoved, closedOrphaned, closedGuildMissing);
    }

    /// <summary>
    /// Returns the LeftAt value to write when closing a stuck session:
    /// either JoinedAt + cap, or now — whichever is earlier. This credits
    /// the user with at most the configured session cap, never more.
    /// </summary>
    private static DateTime ClampLeftAt(DateTime joinedAt, TimeSpan cap, DateTime now)
    {
        var cappedEnd = joinedAt + cap;
        return cappedEnd < now ? cappedEnd : now;
    }
}