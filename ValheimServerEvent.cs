namespace ClanGuardBot.Models;

/// <summary>
/// One server lifecycle transition reported by DiscordConnector — the server
/// finishing startup, or finishing shutdown.
///
/// ── Why this is persisted rather than held in memory ──
/// <c>/valheim-status</c> has to answer "is the server up?" and an in-memory flag
/// would be lost on every bot restart, leaving the command saying "unknown" until
/// the game server next happened to restart. A row survives that.
///
/// ── Why a log rather than a single mutable row ──
/// The same argument as for sessions over a counter: the history answers questions the current value can't. "Is the server
/// restarting more than it should?" is a real question here — the clan spent an
/// evening chasing what turned out to be a moderator installing mods — and a
/// transition log answers it without any new plumbing.
///
/// ── Volume ──
/// Two rows per restart cycle. Even a pathologically flappy server produces tens of
/// rows a day, so this is never pruned; it would take years to become interesting.
/// </summary>
public class ValheimServerEvent
{
    public int Id { get; set; }

    /// <summary>True for a start (server ready for connections), false for a shutdown.</summary>
    public bool Online { get; set; }

    public DateTime OccurredUtc { get; set; }
}
