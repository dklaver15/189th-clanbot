namespace ClanGuardBot.Models;

/// <summary>
/// Persisted snapshot of a currently-present guild member. One row per member;
/// unique on (GuildId, UserId). Maintained by MemberRosterReconciler:
/// inserted/refreshed on UserJoined and on each periodic sweep, and deleted
/// when the member is found to have departed.
///
/// ── Why this table exists ──
/// Discord does not replay UserLeft when the bot reconnects, so any member who
/// leaves while the bot is offline is invisible to the live capture path. By
/// persisting the present-member set, the reconciler can diff it against the
/// live guild on startup and on a schedule, and record the departures it
/// missed. It also caches each member's Discord join timestamp and rank/roles
/// WHILE THEY ARE PRESENT, so a reconciled departure can still report exact
/// tenure and rank even though the member is already gone by detection time.
/// </summary>
public class KnownMember
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>SocketGuildUser.JoinedAt captured while the member is present. Enables exact tenure on a reconciled departure.</summary>
    public DateTime? JoinedAtCached { get; set; }

    /// <summary>Highest rank role name at the last sweep — becomes RankAtDeparture for a reconciled row.</summary>
    public string RankCached { get; set; } = "None";

    /// <summary>CSV of role names (excl. @everyone) at the last sweep.</summary>
    public string RolesCached { get; set; } = string.Empty;

    /// <summary>When this member was first recorded as present.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>When this member was last confirmed present (UserJoined or a sweep).</summary>
    public DateTime LastSeenUtc { get; set; }
}
