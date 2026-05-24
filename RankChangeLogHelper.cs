using ClanGuardBot.Data;
using ClanGuardBot.Models;

namespace ClanGuardBot.Services;

/// <summary>
/// Stages <see cref="RankChange"/> rows on a <see cref="BotDbContext"/>.
/// Mirrors the static-helper convention set by
/// <see cref="EventAttendanceHelper"/> and <see cref="VoiceActivityHelper"/>.
///
/// ── Why staging (not committing) ──
/// Every caller (today: <c>RankTrackingHandler</c>; future: any path that
/// mutates <see cref="RankHistory"/>) is already inside a unit-of-work
/// that ends with its own <c>SaveChangesAsync</c>. Adding our own
/// <c>SaveChangesAsync</c> here would split that into two transactions —
/// a crash between the two would leave <see cref="RankHistory"/> updated
/// without a matching <see cref="RankChange"/> row, breaking the
/// invariant "every rank in <see cref="RankHistory"/> has a corresponding
/// entry in <see cref="RankChange"/>." Single-transaction staging keeps
/// the two tables consistent under all failure modes.
///
/// ── No-op semantics ──
/// Returns without staging when FromRank and ToRank match case-insensitively.
/// This protects against false-positive rank-change events fired by
/// Discord for unrelated role changes — the handler still calls into us,
/// but no spurious row gets written.
/// </summary>
public static class RankChangeLogHelper
{
    /// <summary>
    /// Adds a <see cref="RankChange"/> entity to the context's change-tracker
    /// for the next <c>SaveChangesAsync</c> call.
    /// </summary>
    /// <param name="db">Scoped DbContext; caller is responsible for the flush.</param>
    /// <param name="guildId">Discord guild ID.</param>
    /// <param name="userId">Discord user ID of the member whose rank changed.</param>
    /// <param name="fromRank">Rank before the change, or null for "initial".</param>
    /// <param name="toRank">Rank after the change, or null for "removed".</param>
    /// <param name="changedAt">UTC timestamp of the change.</param>
    public static void StageChange(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        string? fromRank,
        string? toRank,
        DateTime changedAt)
    {
        // Same-rank "change" is a no-op — protects against false-positive
        // GuildMemberUpdated events that don't actually change the rank.
        if (string.Equals(fromRank, toRank, StringComparison.OrdinalIgnoreCase))
            return;

        db.RankChanges.Add(new RankChange
        {
            GuildId   = guildId,
            UserId    = userId,
            FromRank  = fromRank,
            ToRank    = toRank,
            ChangedAt = changedAt,
        });
    }
}
