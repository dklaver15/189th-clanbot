namespace ClanGuardBot.Models;

/// <summary>
/// One play session on the clan's Satisfactory server: a contiguous stretch
/// during which a player was seen online by
/// <see cref="Services.SatisfactoryPresenceService"/>.
///
/// ── Why this exists rather than a running total ──
/// The Ficsit Remote Monitoring API has no join/leave events, so sessions are
/// reconstructed by polling getPlayer and diffing. Storing sessions (not a bare
/// "minutes played" counter) means playtime is always recomputable, a bad poll
/// can be reasoned about after the fact, and "when was X last on?" is answerable.
///
/// ── Why the NAME is the key ──
/// FRM gives two identifiers: <see cref="PlayerId"/> (the character actor id,
/// e.g. "Char_Player_C_2147473237") and <see cref="PlayerName"/> (the platform
/// display name, e.g. "Brandt"). The actor id is more precise but is scoped to
/// the SAVE — starting a new world resets every id, which the clan already did
/// once when moving hosts in July 2026. Keying on the name means clan playtime
/// spans playthroughs, which is the point of tracking it at all.
///
/// The trade: someone who changes their Steam/Epic display name starts a fresh
/// record. That's a rename problem, not a correctness problem, and it's fixable
/// after the fact by updating rows — unlike a save wipe, which is unfixable.
/// PlayerId is still stored, purely for traceability when untangling one.
///
/// ── Open vs closed ──
/// A row with <see cref="EndedUtc"/> = null is OPEN: the player was still online
/// at the last successful poll. <see cref="LastSeenUtc"/> is bumped on every poll
/// they appear in, which is what makes the row survive a bot restart — on boot
/// the service adopts open rows as "currently online" instead of double-counting
/// them, and if the server went down while the bot was off, the session is closed
/// at LastSeenUtc rather than crediting the whole outage as playtime.
///
/// Duration is therefore ALWAYS (EndedUtc ?? LastSeenUtc) - StartedUtc, never
/// (now - StartedUtc) — see <see cref="Duration"/>.
/// </summary>
public class SatisfactorySession
{
    public int Id { get; set; }

    /// <summary>
    /// In-game display name — the identity everything keys on. See the class
    /// remarks for why this rather than <see cref="PlayerId"/>.
    /// </summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>
    /// The character actor id at the time of the session ("Char_Player_C_…").
    /// Save-scoped, so NOT an identity — kept only so a confusing record can be
    /// traced back to a specific character in a specific world.
    /// </summary>
    public string PlayerId { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    /// <summary>Last poll at which this player was seen online. Bumped continuously while open.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Null while the session is open. Set to the leave time when they drop off.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>
    /// Credited playtime. Uses LastSeenUtc (not the wall clock) as the upper
    /// bound so an open session belonging to a dead poller can never inflate
    /// someone's hours.
    /// </summary>
    public TimeSpan Duration => (EndedUtc ?? LastSeenUtc) - StartedUtc;
}
