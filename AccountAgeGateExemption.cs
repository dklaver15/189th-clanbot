namespace ClanGuardBot.Models;

/// <summary>
/// A one-time pass through the account-age gate for a specific Discord user.
/// Added by /allow-new-account (Administrator only) and CONSUMED (deleted) by
/// AccountAgeGateHandler the moment that user next joins with an account still
/// younger than AccountAgeGateMinDays.
///
/// ── Why this exists ──
/// The age gate bans brand-new Discord accounts on join as an anti-raid
/// measure. Occasionally a legitimate recruit genuinely has a fresh account —
/// e.g. a member's spouse who made their first Discord account minutes before
/// joining. This table is the user-ID allowlist the gate's own class comment
/// anticipated: it lets leadership pre-clear that one person so they can be
/// unbanned and rejoin without being caught again.
///
/// ── Why one-time (consumed on use) ──
/// The exemption is deleted as soon as it's used, so it never leaves a standing
/// hole in the gate. A brand-new account also ages past the threshold within a
/// few days on its own, so a permanent bypass would be pointless as well as
/// risky. If the person never rejoins, the row simply sits harmlessly until an
/// admin removes it (or re-adds it, which upserts).
///
/// Unique on (GuildId, UserId): a user is either cleared to join or not; there
/// is no meaning to two exemption rows for the same person. The command upserts
/// on that key and the index is the structural backstop.
///
/// Lives in ClanGuardBot.Models alongside every other entity, following the
/// same convention as PatrolWatchOptOut / KnownMember / SecurityAuditRecord.
/// </summary>
public sealed class AccountAgeGateExemption
{
    public int Id { get; set; }

    /// <summary>Discord guild (server) the exemption applies to.</summary>
    public ulong GuildId { get; set; }

    /// <summary>The Discord user cleared to bypass the age gate on their next join.</summary>
    public ulong UserId { get; set; }

    /// <summary>Discord ID of the admin who added the exemption. Audit trail.</summary>
    public ulong AddedByUserId { get; set; }

    /// <summary>Username of the admin who added the exemption, captured at add time.</summary>
    public string AddedByUsername { get; set; } = string.Empty;

    /// <summary>Optional free-text note supplied by the admin (e.g. "Bravo's wife, brand-new account").</summary>
    public string? Note { get; set; }

    /// <summary>When the exemption was added (UTC).</summary>
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
}
