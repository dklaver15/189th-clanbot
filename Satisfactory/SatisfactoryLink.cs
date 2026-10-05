namespace ClanGuardBot.Models;

/// <summary>
/// Links a Discord member to their Satisfactory in-game name, so playtime and
/// the join feed can say "@Ombrah" instead of a bare player name.
///
/// ── Why a manual link ──
/// FRM exposes an in-game display name and a save-scoped character id, neither
/// of which Discord knows anything about. There is no OAuth handshake and
/// nothing to correlate on, so the mapping is asserted, not proven:
/// <c>/satisfactory-link</c> matches a name against the CURRENTLY ONLINE player
/// list, and officers can link on someone's behalf.
///
/// Consequence: a member has to be online in-game the first time they link.
/// That's a deliberate trade — the alternative is trusting a hand-typed name,
/// which would let anyone claim anyone's playtime.
///
/// ── Keyed on the name, like the sessions ──
/// <see cref="SatisfactoryPlayerName"/> matches
/// <see cref="SatisfactorySession.PlayerName"/>. See that class for why the name
/// beats the character actor id here (the id resets on every new save).
///
/// Unique on (GuildId, DiscordUserId) and (GuildId, SatisfactoryPlayerName): one
/// Discord account per in-game name and vice versa, so re-linking overwrites
/// rather than accumulating duplicates.
/// </summary>
public class SatisfactoryLink
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong DiscordUserId { get; set; }

    /// <summary>The in-game display name — matches SatisfactorySession.PlayerName.</summary>
    public string SatisfactoryPlayerName { get; set; } = string.Empty;

    public DateTime LinkedUtc { get; set; }

    /// <summary>Who ran the link. Equals DiscordUserId for a self-link; an officer's id otherwise.</summary>
    public ulong LinkedByUserId { get; set; }
}
