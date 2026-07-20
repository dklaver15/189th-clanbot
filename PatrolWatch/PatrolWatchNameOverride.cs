namespace ClanGuardBot.Models;

/// <summary>
/// A canonical display name for a member on Patrol Watch embeds, keyed by Discord
/// user ID. When a row exists, the embed shows <see cref="CanonicalName"/> instead
/// of resolving the member's Discord display name; when none exists, the normal
/// name resolution is used unchanged.
///
/// ── Why this exists ──
/// The embed renders <c>SocketGuildUser.DisplayName</c>, whose fallback chain is
/// Nickname → GlobalName → Username. Under heavy rich-presence churn (observed with
/// Palworld on the native Mac build + Game Center), a member's cached Nickname can
/// come back null, so DisplayName silently degrades to the GLOBAL Discord name —
/// e.g. showing "Dklaver" for a member whose guild nickname is "xAP3XRONINx". This
/// override is a deterministic, bot-side fix: it wins regardless of what the cache
/// reports, so an affected member always shows their canonical tag.
///
/// NOT presence/Game-Center data — it's an explicitly curated string set by an
/// officer via /patrol-name. It therefore also doubles as "force this exact tag on
/// the roster line" independent of whatever nickname is set.
///
/// Lives in ClanGuardBot.Models alongside every other entity (same placement as
/// PatrolWatchOptOut) so BotDbContext needs no feature-specific using.
///
/// Scope note: this is a Patrol Watch concern, not a Palworld one — it applies to
/// every tracked game's embed (BF6, Arc Raiders, Helldivers, Palworld, …), because
/// the cache-fallback bug is game-agnostic. Only its most visible trigger is
/// Palworld-on-Mac.
/// </summary>
public sealed class PatrolWatchNameOverride
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    public ulong UserId { get; set; }

    /// <summary>The exact string to render on the roster line. Trimmed; never empty (clear the row instead).</summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>Who last set it — for a light audit trail. Equals the invoking officer.</summary>
    public ulong SetByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
