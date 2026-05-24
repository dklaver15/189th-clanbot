namespace ClanGuardBot.Models;

/// <summary>
/// Append-only log of every rank transition the bot has observed. One row
/// per change. Together with the existing <see cref="RankHistory"/> table
/// (which holds only the CURRENT rank's assignment timestamp), this gives
/// /timeline a full chronological view of a member's rank progression.
///
/// ── Why a separate table ──
/// <see cref="RankHistory"/> is overwritten in place on every rank change
/// (by <c>RankTrackingHandler</c>), so it cannot answer "what ranks did
/// this member hold before their current one?" This table preserves that
/// chronology. Append-only — rows are never updated or deleted by any
/// runtime path. If a backfill needs to be re-run, delete the rows for the
/// affected user(s) and the next startup will re-seed.
///
/// ── Field semantics ──
/// FromRank null → "Initial" transition (member's first observed rank, or
///                  a member who left and rejoined and whose stale
///                  <see cref="RankHistory"/> row had been pruned).
/// ToRank null   → "Removed" transition (member lost all rank roles —
///                  rare, usually only via manual moderator action).
/// Both set      → Promotion or demotion. The direction is computed at
///                  read time by comparing FromRank and ToRank against
///                  <c>BotConfig.RankRoles</c>; storing it would couple
///                  this row to a config value that can shift over time.
///
/// ── Source attribution ──
/// Deliberately omitted. <see cref="RankTrackingHandler"/> is the single
/// chokepoint — every rank role change (manual <c>/promote</c> + <c>/demote</c>,
/// <see cref="ClanGuardBot.Services.AutoPromotionService"/>, and out-of-band
/// Discord role edits by humans) ends up firing
/// <c>GuildMemberUpdated</c>, which routes through that handler. Adding
/// a source string would require a side-channel from
/// <see cref="ClanGuardBot.Services.PromotionService"/> with race
/// conditions for no actionable benefit — officers reviewing a timeline
/// want to know <em>that</em> a promotion happened, not which code path
/// applied it.
///
/// ── ChangedAt vs. RankHistory.AssignedAt ──
/// For the row representing the CURRENT rank, ChangedAt equals
/// RankHistory.AssignedAt by construction. For historical rows, only this
/// table knows the timestamp. Both columns are UTC by contract.
///
/// ── Backfill semantics ──
/// <c>RankChangeBackfillService</c> writes one "Initial" row per existing
/// <see cref="RankHistory"/> row on first startup after this feature
/// deploys. Older transitions that happened before the bot was tracking
/// (or before this table existed) are NOT recoverable — they happened
/// off-record. A member who has been through five promotions before
/// this feature deploys will show one "Initial" row at their current
/// rank's AssignedAt; subsequent promotions will add new rows
/// chronologically.
/// </summary>
public class RankChange
{
    public int Id { get; set; }

    /// <summary>Discord guild (server) ID.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Discord user ID of the member whose rank changed.</summary>
    public ulong UserId { get; set; }

    /// <summary>
    /// Rank name before the change. Null when this row represents the
    /// member's first observed rank (or a rejoin after their stale
    /// <see cref="RankHistory"/> row was pruned). Stored as a plain
    /// string snapshot — does not foreign-key to any rank-role table.
    /// </summary>
    public string? FromRank { get; set; }

    /// <summary>
    /// Rank name after the change. Null when the member lost all rank
    /// roles (rare; usually a manual moderator action). Stored as a
    /// plain string snapshot.
    /// </summary>
    public string? ToRank { get; set; }

    /// <summary>UTC timestamp when the change was observed.</summary>
    public DateTime ChangedAt { get; set; }
}
