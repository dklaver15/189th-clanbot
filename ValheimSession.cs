namespace ClanGuardBot.Models;

/// <summary>
/// One play session on the clan's Valheim server: a contiguous stretch between a
/// join event and the matching leave.
///
/// ── Event-sourced, unlike its Satisfactory counterpart ──
/// <see cref="SatisfactorySession"/> is derived by POLLING the server and diffing snapshots, so every tick re-establishes
/// ground truth and a missed event self-heals within a minute. Valheim has no such
/// poll available: the A2S query surface is silent on this host (crossplay servers
/// register with PlayFab rather than Steam), so these rows are built purely from
/// DiscordConnector's join/leave events.
///
/// That difference has a consequence worth stating plainly: a LOST leave event is
/// not corrected on the next tick, because there is no next tick. The row stays open
/// and would accrue unbounded playtime. Two things bound that damage:
///
///   • <see cref="Services.ValheimEventIngestHandler"/> closes every open session on
///     a server_start event — a crash or restart is exactly when leave events go
///     missing, and the restart itself is the signal that nobody is still connected.
///   • <see cref="Duration"/> is measured to <see cref="LastSeenUtc"/>, never to the
///     wall clock, so an orphaned open row credits only what was observed.
///
/// ── Open vs closed ──
/// <see cref="EndedUtc"/> = null is OPEN: the player joined and hasn't left. Because
/// there is no poll, <see cref="LastSeenUtc"/> is bumped by any event mentioning the
/// player (their own death, for instance) rather than continuously.
/// </summary>
public class ValheimSession
{
    public int Id { get; set; }

    /// <summary>
    /// DiscordConnector's platform-prefixed player id ("STEAM_76561198…", or the
    /// Xbox/Game Pass equivalent) — the stable identity, unaffected by renames.
    ///
    /// <para>Empty when the mod didn't supply one, in which case
    /// <see cref="PlayerName"/> is carrying the identity instead. See
    /// <see cref="Services.ValheimEvent.IdentityKey"/> for why that fallback exists
    /// and what it costs.</para>
    /// </summary>
    public string ValheimPlayerId { get; set; } = string.Empty;

    /// <summary>
    /// Character name, snapshotted per session. Deliberately not normalized into a
    /// player table: names change, and a historical session should show the name in
    /// use at the time.
    /// </summary>
    public string PlayerName { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// Latest moment this player was known to still be connected. Set at join and
    /// bumped by later events for the same identity; it is the upper bound on
    /// credited playtime.
    /// </summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Null while open; set to the leave time otherwise.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>
    /// Credited playtime, bounded by LastSeenUtc rather than the wall clock so an
    /// open row left behind by a missed leave can never inflate someone's hours.
    /// </summary>
    public TimeSpan Duration => (EndedUtc ?? LastSeenUtc) - StartedUtc;
}
