namespace ClanGuardBot.Models;

/// <summary>
/// One play session on the clan's Palworld server: a contiguous stretch during
/// which a player was seen online by <see cref="Services.PalworldPresenceService"/>.
///
/// ── Why this exists rather than a running total ──
/// The REST API has no join/leave events, so sessions are reconstructed by polling
/// /players and diffing. Storing sessions (not a bare "minutes played" counter)
/// means playtime is always recomputable, a bad poll can be reasoned about after
/// the fact, and "when was X last on?" is answerable.
///
/// ── Open vs closed ──
/// A row with <see cref="EndedUtc"/> = null is OPEN: the player was still online at
/// the last successful poll. <see cref="LastSeenUtc"/> is bumped on every poll they
/// appear in, which is what makes the row survive a bot restart — on boot the
/// service adopts open rows as "currently online" instead of double-counting them,
/// and if the server went down while the bot was off, the session is closed at
/// LastSeenUtc rather than crediting the whole outage as playtime.
///
/// Duration is therefore ALWAYS (EndedUtc ?? LastSeenUtc) - StartedUtc, never
/// (now - StartedUtc) — see <see cref="Duration"/>.
/// </summary>
public class PalworldSession
{
    public int Id { get; set; }

    /// <summary>
    /// The platform user id from the API (e.g. "steam_0110000…"). The stable
    /// identity we key everything on: PlayerId changes if a character is recreated,
    /// and names are freely editable, but this does not. May be empty in the
    /// degenerate case where the API only gave us a PlayerId.
    /// </summary>
    public string PalworldUserId { get; set; } = string.Empty;

    /// <summary>Per-character id at the time of the session. Kept for traceability only.</summary>
    public string PalworldPlayerId { get; set; } = string.Empty;

    /// <summary>
    /// In-game character name, snapshotted per session. Deliberately NOT normalized
    /// into a player table: names change, and a historical session should show the
    /// name that was in use at the time (same reasoning as InviteJoin.Username).
    /// </summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>Platform account name, when the API reports one.</summary>
    public string AccountName { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    /// <summary>Last poll at which this player was seen online. Bumped continuously while open.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Null while the session is open. Set to the leave time when they drop off.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Highest character level observed during the session. Powers the level leaderboard.</summary>
    public int Level { get; set; }

    /// <summary>Building count observed at the last poll of this session.</summary>
    public int BuildingCount { get; set; }

    /// <summary>
    /// Credited playtime. Uses LastSeenUtc (not the wall clock) as the upper bound
    /// so an open session belonging to a dead poller can never inflate someone's
    /// hours.
    /// </summary>
    public TimeSpan Duration => (EndedUtc ?? LastSeenUtc) - StartedUtc;
}
