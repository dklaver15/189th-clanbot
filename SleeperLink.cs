namespace ClanGuardBot.Models;

/// <summary>
/// Links a Discord member to their Sleeper account, so fantasy surfaces can say
/// "@Ombrah" instead of a bare Sleeper display name.
///
/// ── How the link is verified ──
/// Sleeper has no OAuth and no handshake, so ownership cannot be proven. What CAN
/// be proven is league membership: <c>/sleeper-link</c> resolves the typed username
/// through <c>GET /user/{username}</c> and then refuses it unless that user id is
/// actually in the configured league. That reduces the trust surface from "anyone
/// on Sleeper" to "one of the sixteen people already in our league", and re-linking
/// overwrites rather than accumulating, so a mistake is self-correcting.
///
/// Same trade as <see cref="SatisfactoryLink"/>: the mapping is asserted, not
/// proven, and officers can link on someone's behalf.
///
/// ── Keyed on the user id, not the username ──
/// <see cref="SleeperUserId"/> is Sleeper's immutable numeric id. The username is
/// stored alongside it for display only, because Sleeper lets people change it and
/// a link keyed on the name would silently break when they did.
///
/// Unique on (GuildId, DiscordUserId) and (GuildId, SleeperUserId): one Discord
/// account per Sleeper account and vice versa.
/// </summary>
public class SleeperLink
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong DiscordUserId { get; set; }

    /// <summary>Sleeper's immutable numeric user id. The join key for everything.</summary>
    public string SleeperUserId { get; set; } = string.Empty;

    /// <summary>
    /// The Sleeper display name at link time. Display only, and allowed to go
    /// stale: live surfaces read the current name from the league users endpoint.
    /// Kept so an unlink or an audit can say who the link pointed at.
    /// </summary>
    public string SleeperUsername { get; set; } = string.Empty;

    public DateTime LinkedUtc { get; set; }

    /// <summary>Who ran the link. Equals DiscordUserId for a self-link; an officer's id otherwise.</summary>
    public ulong LinkedByUserId { get; set; }
}
