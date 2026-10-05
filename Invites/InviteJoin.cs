namespace ClanGuardBot.Models;

/// <summary>
/// One row per UserJoined event the bot observes, with our best-effort
/// attribution to a labeled InviteSource. Append-only; never updated after
/// the initial insert.
///
/// ── Why we snapshot the label ──
/// LabelSnapshot is captured at join time as a plain string copy of the
/// InviteSource.Label that was active when the user arrived. If an officer
/// later renames the source (or revokes it, or even hard-deletes it from
/// the DB someday), every historical join row keeps its original label —
/// the recruit who joined via "Website" in May still shows up under
/// "Website" in next year's reports. The optional InviteCode foreign key
/// (not enforced as a real FK to keep cleanup flexible) is kept for
/// traceability only.
///
/// ── Attribution outcomes ──
/// Exactly one of these LabelSnapshot values is written:
///   • A real label, e.g. "Website"  — clean attribution to a known InviteSource.
///   • "Vanity"                       — vanity URL use count incremented by 1.
///   • "Unknown"                      — a regular invite's count incremented by 1
///                                      but we have no InviteSource for that code.
///                                      Officer can run /invite assign to label it
///                                      retroactively (future joins) without
///                                      mutating this historical row.
///   • "Ambiguous"                    — multiple invite use counts incremented
///                                      between two UserJoined events. Discord
///                                      gives us no way to disambiguate. IsAmbiguous
///                                      is set true and InviteCode is null.
///   • "Unattributed"                 — no use count anywhere went up. Likely a
///                                      single-use invite that was already deleted
///                                      before our event handler fetched the list,
///                                      or a join via some method we don't track
///                                      (e.g. server discovery, where the bot has
///                                      no signal at all).
/// </summary>
public class InviteJoin
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    /// <summary>The Discord ID of the user who joined.</summary>
    public ulong UserDiscordId { get; set; }

    /// <summary>
    /// Display name snapshot at join time (server display name → global name →
    /// underlying username, in that order of preference). Captured for the same
    /// reason EventAttendance.Username is — historical reports need a human
    /// label even for users who later left the server.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// The Discord invite code that was used. Null when we couldn't attribute
    /// (Unknown / Ambiguous / Unattributed). Not a real foreign key — the
    /// matching InviteSource row may be inactive or, in degenerate scenarios,
    /// missing entirely. Use this for traceability and for /invite info drill-downs.
    /// </summary>
    public string? InviteCode { get; set; }

    /// <summary>
    /// Frozen copy of InviteSource.Label at join time, OR a special sentinel
    /// ("Vanity", "Unknown", "Ambiguous", "Unattributed"). Always populated.
    /// Reports group by this column; never join back to InviteSource for the
    /// label.
    /// </summary>
    public string LabelSnapshot { get; set; } = string.Empty;

    /// <summary>
    /// The Discord user who created the invite (from IInviteMetadata.Inviter).
    /// Null for vanity, ambiguous, unknown, or any case where the inviter
    /// couldn't be resolved. Used for the "top member referrers" briefing
    /// section so we can credit individual members for their personal links.
    /// </summary>
    public ulong? InviterDiscordId { get; set; }

    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// True when more than one tracked invite's use count went up between the
    /// previous cache snapshot and this join. We can't tell which one this user
    /// used; the row exists for completeness but should be excluded from
    /// per-source attribution stats.
    /// </summary>
    public bool IsAmbiguous { get; set; }
}
