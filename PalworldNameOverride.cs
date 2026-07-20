namespace ClanGuardBot.Models;

/// <summary>
/// A canonical display name for a Palworld player, keyed by the stable platform
/// <see cref="PalworldUserId"/>. When a row exists, the join/leave feed,
/// /palworld-playtime and /palworld-leaderboard show <see cref="CanonicalName"/>
/// instead of the in-game character name the server reports; when none exists, the
/// raw reported name is used unchanged.
///
/// ── Why this exists ──
/// The Palworld server reports whatever identity a session is under. The same
/// account (identical PalworldUserId) shows as "xAP3XRONINx" over Steam but as the
/// Game Center identity "Dklaver" on the native Mac build. That's purely a display
/// difference — playtime already aggregates correctly on the UserId — but it makes
/// the feed flip a member's name between sessions. This override pins the clan
/// gamertag so every surface reads the same regardless of platform.
///
/// ── Keyed by PalworldUserId, NOT (Guild, Discord) ──
/// Deliberately server-global with no GuildId, matching <see cref="PalworldSession"/>
/// (the clan runs one server, and the feed posts to a single channel). It also does
/// NOT depend on <see cref="PalworldLink"/>: a member need not have linked their
/// Discord account for their in-game name to be corrected in the feed.
/// </summary>
public sealed class PalworldNameOverride
{
    public int Id { get; set; }

    /// <summary>The platform user id (e.g. "mac_A:_acae…") — matches PalworldSession.PalworldUserId. Unique.</summary>
    public string PalworldUserId { get; set; } = string.Empty;

    /// <summary>The exact string to display. Trimmed; never empty (clear the row instead).</summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>The Discord id of the officer who set it — light audit trail.</summary>
    public ulong SetByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
