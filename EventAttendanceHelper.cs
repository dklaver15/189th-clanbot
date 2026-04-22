using ClanGuardBot.Data;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// Counts how many clan events a member attended in a given time window.
///
/// Reads directly from the EventAttendance table, which is populated by
/// EventAttendanceSnapshotService shortly after each event ends. All the
/// heavy lifting (matching voice sessions to event windows, applying buffers
/// and session caps, filtering to the events VC) happens at snapshot time,
/// so this query is just "count rows in the time window."
///
/// ── Why not compute attendance inline like before ──
/// CalendarEvents rows get deleted when Apollo messages are cleaned up from
/// #events. That makes them unreliable as a source of truth for past events.
/// EventAttendance rows have denormalized event times and are never deleted
/// by any lifecycle event, so attendance history survives any cleanup.
///
/// ── Time zones ──
/// EventEndUtc is UTC by contract. rankAssignedAt comes from RankHistory which
/// is also UTC. No TZ math needed.
/// </summary>
public static class EventAttendanceHelper
{
    public static async Task<int> CountEventsAttendedAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        DateTime rankAssignedAt,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        return await db.EventAttendances
            .CountAsync(a => a.GuildId  == guildId
                             && a.UserId   == userId
                             && a.EventEndUtc >= rankAssignedAt
                             && a.EventEndUtc <= now, ct);
    }
}