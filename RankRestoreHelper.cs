using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// Recovers the date a member originally received the rank they currently
/// hold, when the RankHistory row that recorded it was destroyed by the rank
/// role coming off and going back on.
///
/// Static helper in the style of <see cref="EventAttendanceHelper"/>,
/// <see cref="VoiceActivityHelper"/> and <see cref="RankChangeLogHelper"/>:
/// no state, takes a <see cref="BotDbContext"/>, never calls SaveChanges.
///
/// ── The problem it solves ──
/// A member holding no rank role at all has their RankHistory row DELETED by
/// <c>RankTrackingHandler</c> — that deletion is the bot's only representation
/// of "unranked". Nothing in ClanGuard takes a member's last rank role away, so
/// it always means a human or a reaction role did. When the role goes back on,
/// the member looks brand new and gets a fresh row dated now. That silently
/// restarts their time-in-rank AND zeroes their activity at that rank, because
/// every promotion input — MessageEvents, <see cref="VoiceActivityHelper"/>,
/// <see cref="EventAttendanceHelper"/> — filters on <c>&gt;= AssignedAt</c>.
/// The member shows 0 messages, 0 voice hours and 0 events at a rank they have
/// held for weeks, and the nightly auto-promotion agrees with that reading.
///
/// ── Why the real date survives ──
/// <see cref="RankChange"/> is append-only; no runtime path updates or deletes
/// from it. The assignment that preceded the loss is therefore still on record
/// even though the RankHistory row that mirrored it is gone.
///
/// ── Two callers, one walk ──
/// <c>RankTrackingHandler</c> calls this live, at the moment the role returns,
/// when the newest log row is the removal. <c>/fix-rank-date</c> calls it after
/// the fact, when a restore assignment has already been appended on top. Both
/// are handled by skipping a leading assignment row, so the pairing logic
/// exists exactly once and the command can never disagree with the automatic
/// repair about what the right date is.
/// </summary>
public static class RankRestoreHelper
{
    /// <summary>
    /// How many rank-change rows to read. Each lost-and-restored round trip
    /// costs two rows, so this covers a role that has flapped fifteen times —
    /// far past anything seen in practice — while keeping the lookup a single
    /// bounded seek on the (GuildId, UserId, ChangedAt) index.
    /// </summary>
    public const int MaxChainRows = 32;

    /// <summary>
    /// Outcome of the lookup. <paramref name="OriginalAssignedAt"/> is null
    /// whenever no repair is warranted; the other fields say why, so callers
    /// can log it or show it to an officer instead of just reporting nothing.
    /// </summary>
    /// <param name="OriginalAssignedAt">
    /// The date the member actually received this rank, or null when the log
    /// shows no same-rank round trip (or one was found but rejected).
    /// </param>
    /// <param name="LostAt">
    /// When the most recent loss of this rank was recorded, if one was found.
    /// Populated even when the result is rejected, so the caller can explain.
    /// </param>
    /// <param name="RejectedByJoinDate">
    /// True when a candidate was found but predates the member's current
    /// guild membership, so it was discarded. See the leave/rejoin note below.
    /// </param>
    /// <param name="RejectedByWindow">
    /// True when a candidate loss was found but is older than the caller's
    /// window. The date is real; the caller simply declined to reach that far
    /// back automatically.
    /// </param>
    public record Result(
        DateTime? OriginalAssignedAt,
        DateTime? LostAt,
        bool RejectedByJoinDate = false,
        bool RejectedByWindow = false);

    private static readonly Result None = new(null, null);

    /// <summary>
    /// Reads the rank-change log for one member and returns the assignment
    /// date to restore, or a <see cref="Result"/> explaining why there is none.
    ///
    /// ── What matches ──
    /// Rows are paired off newest-first: a "Removed" row whose FromRank is the
    /// rank being held now, then the assignment that put that rank there. A
    /// role that flapped several times leaves several such pairs, so the walk
    /// continues through them to reach the assignment at the bottom of the
    /// chain rather than stopping at an earlier restore.
    ///
    /// ── What deliberately does NOT match ──
    /// Promotions and demotions pass through a rank-less moment too:
    /// <c>PromotionService</c> and <c>/demote</c> strip the old rank role
    /// before adding the new one, so the row really is deleted and re-created
    /// there as well. But the removal row's FromRank is the rank being LEFT
    /// BEHIND, not the one arriving, so the first comparison fails and a
    /// promotion correctly starts its clock at now. This is the single most
    /// important property of the walk — without it every promotion would
    /// inherit the previous rank's tenure.
    ///
    /// ── Leave/rejoin ──
    /// <see cref="RankChange"/> rows survive a member leaving the guild; only
    /// RankHistory is pruned (by <c>MemberLifecycleHandler</c>). A candidate
    /// earlier than <paramref name="joinedAt"/> is therefore from a previous
    /// stint and is rejected, so a returning member cannot inherit tenure they
    /// no longer have. Same rule as the leave/rejoin defense in
    /// <c>PromotionService.GetRankInfoAsync</c>.
    ///
    /// ── Not recovered ──
    /// The seed fields (EventsAttendedAtRankBeforeBot, SeedAppliedAt) lived
    /// only on the deleted row and are not in the change log. A member who had
    /// spreadsheet event credit at this rank needs a fresh
    /// <c>/seed-promotion-credit</c> run. Callers should say so.
    /// </summary>
    /// <param name="db">Scoped DbContext. Read-only; nothing is staged.</param>
    /// <param name="guildId">Discord guild ID.</param>
    /// <param name="userId">Discord user ID of the member being repaired.</param>
    /// <param name="rank">The rank the member holds now — the one whose loss we are undoing.</param>
    /// <param name="now">Reference time for the window check.</param>
    /// <param name="window">
    /// How far back a loss may be and still be repaired, or null for no limit.
    /// Only the most recent loss is time-boxed; the older pairs in a flap chain
    /// were already inside the window when they happened. The live handler
    /// passes BotConfig.RankRestoreWindowDays; an officer running
    /// <c>/fix-rank-date</c> by hand passes null, because they are looking at
    /// the specific member and have already decided the repair is warranted.
    /// </param>
    /// <param name="joinedAt">The member's current guild-join timestamp, if known.</param>
    public static async Task<Result> ResolveOriginalAssignmentAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        string rank,
        DateTime now,
        TimeSpan? window,
        DateTime? joinedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rank)) return None;
        if (window is { Ticks: <= 0 }) return None;

        var changes = await db.RankChanges
            .AsNoTracking()
            .Where(c => c.GuildId == guildId && c.UserId == userId)
            .OrderByDescending(c => c.ChangedAt)
            .ThenByDescending(c => c.Id)
            .Take(MaxChainRows)
            .ToListAsync(ct);

        if (changes.Count == 0) return None;

        // The live handler arrives with the removal on top (its own assignment
        // row has not been staged yet). /fix-rank-date arrives after that row
        // was written, so the restore assignment sits on top instead. Skipping
        // a leading assignment normalizes both to "removal first".
        var start = IsAssignmentOf(changes[0], rank) ? 1 : 0;

        var cutoff = window.HasValue ? now - window.Value : (DateTime?)null;
        DateTime? restored = null;
        DateTime? lostAt = null;

        for (int i = start; i + 1 < changes.Count; i += 2)
        {
            var removal = changes[i];

            // Must be a removal of the very rank being held now. Anything else
            // — a promotion's removal of the previous rank, a stray assignment
            // — ends the chain.
            if (removal.ToRank is not null ||
                !string.Equals(removal.FromRank, rank, StringComparison.OrdinalIgnoreCase))
                break;

            // Time-box only the loss actually being undone, not the whole chain.
            if (restored is null && cutoff.HasValue && removal.ChangedAt < cutoff.Value)
                return new Result(null, removal.ChangedAt, RejectedByWindow: true);

            var assignment = changes[i + 1];
            if (!IsAssignmentOf(assignment, rank)) break;

            lostAt ??= removal.ChangedAt;
            restored = assignment.ChangedAt;
        }

        if (restored is null) return new Result(null, lostAt);

        if (joinedAt.HasValue && restored.Value < joinedAt.Value)
            return new Result(null, lostAt, RejectedByJoinDate: true);

        return new Result(restored, lostAt);
    }

    /// <summary>
    /// True when the row records this rank being put ON — either a first
    /// assignment (FromRank null) or a transition into it from another rank.
    /// </summary>
    private static bool IsAssignmentOf(RankChange change, string rank) =>
        string.Equals(change.ToRank, rank, StringComparison.OrdinalIgnoreCase);
}
