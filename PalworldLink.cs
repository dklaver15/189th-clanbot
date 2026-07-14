namespace ClanGuardBot.Models;

/// <summary>
/// Links a Discord member to their Palworld identity, so playtime and the join
/// feed can say "@Ombrah" instead of a bare in-game name.
///
/// ── Why a manual link ──
/// The REST API exposes a platform user id and an in-game name, neither of which
/// Discord knows anything about. There is no OAuth handshake, no verification
/// endpoint, nothing to correlate on. So the mapping is asserted, not proven:
/// <c>/palworld-link</c> matches a name against the CURRENTLY ONLINE player list
/// (the only way we can see anyone's UserId), and officers can link on someone's
/// behalf.
///
/// Consequence: a member has to be online in-game the first time they link. That's
/// a deliberate trade — the alternative is trusting a hand-typed steam_… id, which
/// would let anyone claim anyone's playtime.
///
/// Unique on (GuildId, DiscordUserId) and (GuildId, PalworldUserId): one Discord
/// account per Palworld identity and vice versa, so re-linking overwrites rather
/// than accumulating duplicates.
/// </summary>
public class PalworldLink
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong DiscordUserId { get; set; }

    /// <summary>The platform user id (e.g. "steam_0110000…") — matches PalworldSession.PalworldUserId.</summary>
    public string PalworldUserId { get; set; } = string.Empty;

    /// <summary>In-game name at link time. Display convenience; sessions carry their own snapshot.</summary>
    public string PalworldName { get; set; } = string.Empty;

    public DateTime LinkedUtc { get; set; }

    /// <summary>Who ran the link. Equals DiscordUserId for a self-link; an officer's id otherwise.</summary>
    public ulong LinkedByUserId { get; set; }
}
