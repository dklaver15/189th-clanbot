namespace ClanGuardBot.Models;

/// <summary>
/// Per-member opt-out from Patrol Watch. Row present = opted out;
/// no row = opted in (the default). Toggled by /patrol off and /patrol on.
///
/// Lives in ClanGuardBot.Models alongside every other entity (RedditLead,
/// InviteSource, etc. all live here regardless of which feature folder
/// their service code sits in) so BotDbContext doesn't need a feature-
/// specific using to reference the type.
///
/// ── Why a single-purpose table ──
/// A generic "MemberPreferences" table was considered and rejected for v1.
/// Patrol Watch is the only opt-out toggle today. If a second toggle ever
/// shows up (Sarge, weekly DMs), refactor then. YAGNI now.
/// </summary>
public sealed class PatrolWatchOptOut
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public DateTime OptedOutAtUtc { get; set; }
}
