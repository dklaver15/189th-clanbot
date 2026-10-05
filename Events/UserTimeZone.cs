namespace ClanGuardBot.Models;

/// <summary>
/// A member's preferred timezone, used ONLY to interpret natural-language time
/// input when they create an event ("7pm tomorrow" means 7pm in this zone).
/// Set via /timezone or captured by the creation wizard on first use; falls
/// back to BotConfig.EventDefaultTimeZone when unset.
///
/// ── Not used for display ──
/// The #events embed localizes per viewer automatically via Discord
/// &lt;t:unix&gt; markdown, so we never need a viewer's timezone — only the
/// organizer's, and only at input time. Timezone is a property of the person,
/// not the guild, so this is keyed on UserId alone (no GuildId) and shared
/// across any guild the bot serves.
/// </summary>
public class UserTimeZone
{
    public int Id { get; set; }

    public ulong UserId { get; set; }

    /// <summary>IANA timezone id, e.g. "America/Chicago".</summary>
    public string IanaId { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
