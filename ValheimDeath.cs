namespace ClanGuardBot.Models;

/// <summary>
/// One death on the clan's Valheim server.
///
/// ── Why a row per death rather than a counter ──
/// A counter answers "how many times has X died" and nothing else. Rows also answer
/// "who died most this week", "was last night unusually bloody", and survive a
/// recount if the ingest logic ever changes — the same argument
/// <see cref="PalworldSession"/> makes for storing sessions instead of a minutes
/// total. Deaths are low-volume (a busy Valheim night is tens, not thousands), so
/// the storage cost is irrelevant.
///
/// ── No position ──
/// DiscordConnector can attach coordinates to death messages, but the ingest format
/// deliberately turns that off ("Send Positions with Messages = false"): broadcasting
/// where someone's corpse and gear are sitting is player-visible information that
/// affects the game, and it isn't needed for any statistic here.
/// </summary>
public class ValheimDeath
{
    public int Id { get; set; }

    /// <summary>Platform-prefixed player id; empty when the mod didn't supply one.</summary>
    public string ValheimPlayerId { get; set; } = string.Empty;

    /// <summary>Character name at time of death.</summary>
    public string PlayerName { get; set; } = string.Empty;

    public DateTime DiedUtc { get; set; }
}
