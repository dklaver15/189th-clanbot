using ClanGuardBot.Data;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// Counts how many clan events a member has accumulated toward promotion at
/// their current rank, combining bot-tracked EventAttendance rows AND
/// MeetingAttendance rows with any one-time seed credit from the manual
/// promotion spreadsheet.
///
/// Reads directly from the EventAttendance and MeetingAttendance tables,
/// both populated by their respective snapshot services shortly after each
/// event ends. All the heavy lifting (matching voice sessions to event
/// windows, applying buffers and session caps, filtering to the correct
/// channel/category) happens at snapshot time, so this query is just
/// "count rows in the time window plus seed."
///
/// ── Why meetings are summed in ──
/// By clan policy "attending a meeting counts as an event" for promotion
/// purposes. The simplest way to make every consumer (AutoPromotionService,
/// PromoEligibilityCommandHandler, weekly briefing's promotion-candidate
/// path) inherit this rule with no per-call changes is to fold the meeting
/// count into the same helper.
///
/// ── Double-count edge case ──
/// If a user qualifies for BOTH the events-VC threshold AND the meetings-VC
/// threshold on the same Apollo event (which would require them to spend
/// meaningful time in two separate VCs during the same buffered window),
/// they will be credited twice for that event. This is rare in practice —
/// meetings and events typically use distinct VCs and distinct Apollo posts.
/// If it ever becomes a real problem the dedupe can be added here by
/// distincting CalendarEventIds across the two tables before counting.
///
/// ── Why not compute attendance inline like before ──
/// CalendarEvents rows get deleted when Apollo messages are cleaned up from
/// #events. That makes them unreliable as a source of truth for past events.
/// EventAttendance and MeetingAttendance rows have denormalized event times
/// and are never deleted by any lifecycle event, so attendance history
/// survives any cleanup.
///
/// ── Seeding ──
/// Any events attended before the bot became authoritative are captured via
/// the one-time seed applied by /seed-promotion-credit (sourced from the
/// "Seed Events" column on the roster sheet). When a seed is present, only
/// bot-tracked rows with EventEndUtc &gt;= SeedAppliedAt are counted, since
/// events before that moment are assumed to already be reflected in the seed.
/// The same rule applies to MeetingAttendance — there is no separate meeting
/// seed today; meetings before the bot tracked them are not retroactively
/// counted.
///
/// ── Time zones ──
/// EventEndUtc is UTC by contract. rankAssignedAt and seedAppliedAt are UTC
/// (RankHistory stores UTC throughout). No TZ math needed.
/// </summary>
public static class EventAttendanceHelper
{
    /// <summary>
    /// Returns the total events-at-rank count for promotion math:
    /// seed value + bot-tracked EventAttendance rows + bot-tracked
    /// MeetingAttendance rows between the effective "since" date (the later
    /// of rankAssignedAt and seedAppliedAt) and now.
    /// </summary>
    /// <param name="rankAssignedAt">When the user got their current rank.</param>
    /// <param name="seedAppliedAt">When the one-time spreadsheet seed was
    /// applied. Null means no seed has been applied at this rank.</param>
    /// <param name="seedEvents">Count of events already credited via the seed.
    /// Added to the bot-tracked count to produce the total.</param>
    public static async Task<int> CountEventsAttendedAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        DateTime rankAssignedAt,
        DateTime? seedAppliedAt,
        int seedEvents,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Use the later of (rank assignment, seed applied) as the "since"
        // cutoff. If a seed is present and was applied after the rank was
        // assigned, events before the seed were already counted by the human
        // who filled in the spreadsheet, so we must NOT count them again here.
        var effectiveSince = seedAppliedAt.HasValue && seedAppliedAt.Value > rankAssignedAt
            ? seedAppliedAt.Value
            : rankAssignedAt;

        var botTrackedCount = await db.EventAttendances
            .CountAsync(a => a.GuildId == guildId
                          && a.UserId == userId
                          && a.EventEndUtc >= effectiveSince
                          && a.EventEndUtc <= now, ct);

        var meetingCount = await db.MeetingAttendances
            .CountAsync(a => a.GuildId == guildId
                          && a.UserId == userId
                          && a.EventEndUtc >= effectiveSince
                          && a.EventEndUtc <= now, ct);

        return seedEvents + botTrackedCount + meetingCount;
    }
}