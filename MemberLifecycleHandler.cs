using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Cleans up per-member promotion state when a user leaves the guild.
///
/// ── Why this exists ──
/// RankHistory rows are keyed by (GuildId, UserId) and track when a user's
/// current rank was assigned. They are the source of truth for the nightly
/// AutoPromotionService's time-in-rank and message-activity windows
/// (MessageEvents are filtered by `Timestamp >= RankHistory.AssignedAt`).
///
/// Historically, leaving the guild did NOT touch RankHistory. When a former
/// member rejoined later and was re-assigned RCT, the stale row from their
/// previous tenure would still report the old AssignedAt, and the
/// auto-promotion job could promote them based on time and messages from a
/// prior membership that no longer existed. RankTrackingHandler does reset
/// AssignedAt on the RCT-add event, but only when `beforeCacheable.HasValue`
/// is true at the time — if the bot was offline or the user wasn't cached
/// when the role was re-added, the reset never happens.
///
/// PromotionService.GetRankInfoAsync now has a defensive read-side check
/// (compares JoinedAt to AssignedAt) so promotion math is safe even when a
/// stale row exists, but cleaning up the row on leave keeps the DB honest
/// and avoids surprising future code that reads RankHistory directly.
///
/// ── Scope ──
/// Only RankHistory is removed here. Other per-user tables (MessageEvents,
/// VoiceSessions, EventAttendance, AwolRecords, InviteJoins, etc.) are
/// intentionally left alone — they're audit/analytics data that survives
/// membership transitions by design, and several have their own age-based
/// prune jobs.
///
/// ── Failure handling ──
/// Failures here are logged but never thrown — a DB hiccup on a leave event
/// must not crash the gateway. The GetRankInfoAsync defense still protects
/// the auto-promotion path if this cleanup pass misses a leave.
/// </summary>
public class MemberLifecycleHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MemberLifecycleHandler> _logger;

    public MemberLifecycleHandler(
        IServiceProvider services,
        ILogger<MemberLifecycleHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Register the UserLeft event handler on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.UserLeft += OnUserLeft;
    }

    private Task OnUserLeft(SocketGuild guild, SocketUser user)
    {
        // Fire-and-forget; gateway handlers must not block the event loop.
        _ = CleanupRankHistoryAsync(guild.Id, user.Id, user.Username);
        return Task.CompletedTask;
    }

    private async Task CleanupRankHistoryAsync(ulong guildId, ulong userId, string username)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var removed = await db.RankHistories
                .Where(r => r.GuildId == guildId && r.UserId == userId)
                .ExecuteDeleteAsync();

            if (removed > 0)
            {
                _logger.LogInformation(
                    "Cleaned up {Count} RankHistory row(s) for departing user {Username} ({UserId}) in guild {GuildId}",
                    removed, username, userId, guildId);
            }
            else
            {
                _logger.LogDebug(
                    "No RankHistory row to clean up for departing user {Username} ({UserId}) in guild {GuildId}",
                    username, userId, guildId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to clean up RankHistory for departing user {Username} ({UserId}) in guild {GuildId}",
                username, userId, guildId);
        }
    }
}
