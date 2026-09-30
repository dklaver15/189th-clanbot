using ClanGuardBot.Data;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// Shared voice-activity math used by both AwolCheckService and AutoPromotionService.
/// Applies a per-session duration cap so stuck sessions (sessions that were never
/// properly closed because the bot crashed or lost gateway connection) cannot inflate
/// a user's activity totals beyond plausible values.
///
/// Without this cap, a single orphaned "open" session could accumulate hundreds of
/// hours over weeks and cause false activity credit. See VoiceSessionCleanupService
/// for the startup repair that actively closes such sessions.
/// </summary>
public static class VoiceActivityHelper
{
    /// <summary>
    /// Calculates total voice seconds a user has logged within a window, applying
    /// a per-session cap to each session's contribution.
    /// </summary>
    /// <param name="db">Scoped DbContext.</param>
    /// <param name="guildId">Guild to query.</param>
    /// <param name="userId">User to query.</param>
    /// <param name="windowStart">Cutoff — sessions before this are only counted
    /// for their overlap with the window.</param>
    /// <param name="maxSingleSessionHours">Per-session duration cap. A single
    /// session contributes at most this many hours regardless of its actual
    /// (JoinedAt → LeftAt) duration. Set to 0 or negative to disable the cap.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total voice seconds within the window, with per-session cap applied.</returns>
    public static async Task<long> GetVoiceSecondsAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        DateTime windowStart,
        double maxSingleSessionHours,
        CancellationToken ct = default)
    {
        var capSeconds = maxSingleSessionHours > 0
            ? (long)(maxSingleSessionHours * 3600)
            : long.MaxValue;

        long totalSeconds = 0;

        // ── Sessions that started within the window ──
        var sessionsInWindow = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.UserId == userId
                     && v.JoinedAt >= windowStart)
            .ToListAsync(ct);

        foreach (var session in sessionsInWindow)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            var sessionDuration = (long)(end - session.JoinedAt).TotalSeconds;
            totalSeconds += Math.Min(sessionDuration, capSeconds);
        }

        // ── Sessions that started before the window but overlap into it ──
        // These are counted only for the portion inside the window, with the cap
        // still applied to the windowed portion (so a massively stuck pre-window
        // session can't dump hundreds of hours into a 7-day activity tally).
        var sessionsOverlapping = await db.VoiceSessions
            .Where(v => v.GuildId == guildId
                     && v.UserId == userId
                     && v.JoinedAt < windowStart
                     && (v.LeftAt == null || v.LeftAt > windowStart))
            .ToListAsync(ct);

        foreach (var session in sessionsOverlapping)
        {
            var end = session.LeftAt ?? DateTime.UtcNow;
            var windowedDuration = (long)(end - windowStart).TotalSeconds;
            totalSeconds += Math.Min(windowedDuration, capSeconds);
        }

        return totalSeconds;
    }

    /// <summary>
    /// LeftAt for closing a session whose real leave time is unknown (a missed
    /// leave event): JoinedAt + the per-session cap, or <paramref name="end"/>,
    /// whichever is earlier. Credits at most the cap, never more.
    /// </summary>
    public static DateTime ClampLeftAt(DateTime joinedAt, double maxSingleSessionHours, DateTime end)
    {
        if (maxSingleSessionHours <= 0) return end;
        var cappedEnd = joinedAt + TimeSpan.FromHours(maxSingleSessionHours);
        return cappedEnd < end ? cappedEnd : end;
    }
}