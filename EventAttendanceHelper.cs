using ClanGuardBot.Data;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// Counts how many clan events a member attended based on their time in the
/// events voice channel during buffered event windows.
///
/// "Attended" means: the member had at least <paramref name="minAttendanceMinutes"/>
/// of cumulative voice time in the events VC during
/// [event.StartUtc - bufferMinutes, event.EndUtc + bufferMinutes].
///
/// ── Channel matching ──
/// Filters by VoiceSession.ChannelId (ulong?). That column is nullable and may
/// be NULL on rows written before ChannelId was introduced — those rows won't
/// match the filter. For CPL→SGT (14-day lookback) this is a non-issue in
/// practice, but if a future tier looks further back we may need to backfill.
///
/// ── Why the CalendarEvents DB table, not the Google Calendar API ──
/// Google Calendar drops past events (they age out once done), so querying the
/// live calendar will miss everything a user already attended. The CalendarEvents
/// table in our DB persists those records as long as the originating Apollo
/// Discord message is still present in #events. That makes it the durable
/// source of truth for historical attendance — but it also means that if old
/// Apollo messages ever get purged, the corresponding DB rows go with them.
///
/// ── Per-session cap ──
/// We apply the same MaxSingleSessionHours cap VoiceActivityHelper uses so a
/// single stuck/orphaned session (e.g. bot crashed while the user was in VC)
/// can't inflate attendance credit across multiple events.
///
/// ── Time zones ──
/// All DateTime values here are UTC: CalendarEvent.StartUtc/EndUtc are UTC by
/// contract, and VoiceSession.JoinedAt/LeftAt are written from DateTime.UtcNow.
/// Member local timezone is irrelevant — everything compares UTC-to-UTC.
/// </summary>
public static class EventAttendanceHelper
{
    public static async Task<int> CountEventsAttendedAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        ulong eventsVoiceChannelId,
        DateTime rankAssignedAt,
        int bufferMinutes,
        int minAttendanceMinutes,
        double maxSingleSessionHours,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Only events that already finished and fell inside the member's current
        // rank tenure count — this matches how msgs/voice are already scoped.
        var events = await db.CalendarEvents
            .Where(c => c.GuildId == guildId
                     && c.EndUtc  >= rankAssignedAt
                     && c.EndUtc  <= now)
            .ToListAsync(ct);

        if (events.Count == 0) return 0;

        var buffer           = TimeSpan.FromMinutes(bufferMinutes);
        var thresholdSeconds = (long)minAttendanceMinutes * 60;
        var capSeconds       = maxSingleSessionHours > 0
            ? (long)(maxSingleSessionHours * 3600)
            : long.MaxValue;

        var attended = 0;

        foreach (var evt in events)
        {
            var winStart = evt.StartUtc - buffer;
            var winEnd   = evt.EndUtc   + buffer;

            // All sessions in the events VC that overlap the buffered window.
            // "Overlap" = session joined before window ended AND session hadn't
            // left before window started.
            var sessions = await db.VoiceSessions
                .Where(v => v.GuildId   == guildId
                         && v.UserId    == userId
                         && v.ChannelId == eventsVoiceChannelId
                         && v.JoinedAt  <  winEnd
                         && (v.LeftAt   == null || v.LeftAt > winStart))
                .ToListAsync(ct);

            long totalSecondsInWindow = 0;

            foreach (var s in sessions)
            {
                var sessionEnd = s.LeftAt ?? now;

                // Clip the session to the buffered event window so a 5-hour
                // session that only overlaps the event by 20 min counts as 20 min.
                var overlapStart = s.JoinedAt > winStart ? s.JoinedAt  : winStart;
                var overlapEnd   = sessionEnd < winEnd   ? sessionEnd : winEnd;
                var overlapSec   = (long)(overlapEnd - overlapStart).TotalSeconds;

                if (overlapSec > 0)
                    totalSecondsInWindow += Math.Min(overlapSec, capSeconds);
            }

            if (totalSecondsInWindow >= thresholdSeconds)
                attended++;
        }

        return attended;
    }
}