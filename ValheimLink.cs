namespace ClanGuardBot.Models;

/// <summary>
/// Links a Discord member to their Valheim identity, so playtime and the
/// leaderboard can name the member rather than a bare character name.
///
/// ── Why linking here is easier than for Palworld ──
/// <see cref="PalworldLink"/> can only be created while the player is ONLINE,
/// because the platform id is visible solely in the live player list — which forces
/// members to be in game the first time they link. DiscordConnector instead reports
/// the player id on every join, so by the time anyone runs the link command the id
/// is already on file from their session history. Linking therefore works offline,
/// against any character the server has seen.
///
/// ── Still asserted, not proven ──
/// Same caveat as the other two games: nothing cryptographically ties a Discord
/// account to a character, so the mapping is a claim. Self-linking is allowed on the
/// honour system within a clan server, and officers can link on someone's behalf to
/// correct mistakes.
///
/// Unique in both directions on (GuildId, DiscordUserId) and
/// (GuildId, ValheimPlayerId), so re-linking overwrites instead of accumulating
/// duplicate claims — matching the Palworld and Satisfactory link tables.
/// </summary>
public class ValheimLink
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong DiscordUserId { get; set; }

    /// <summary>Matches <see cref="ValheimSession.ValheimPlayerId"/>.</summary>
    public string ValheimPlayerId { get; set; } = string.Empty;

    /// <summary>Character name at link time. Display convenience; sessions carry their own snapshot.</summary>
    public string ValheimName { get; set; } = string.Empty;

    public DateTime LinkedUtc { get; set; }

    /// <summary>Who ran the link. Equals DiscordUserId for a self-link; an officer's id otherwise.</summary>
    public ulong LinkedByUserId { get; set; }
}
