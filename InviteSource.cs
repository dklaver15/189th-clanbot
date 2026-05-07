namespace ClanGuardBot.Models;

/// <summary>
/// One row per labeled Discord invite. Created either by /invite create
/// (live creation) or /invite assign (retroactive labeling of an invite that
/// already exists, e.g. one made via the Discord UI before the bot was added).
///
/// ── Why labels live in our DB and not in Discord ──
/// Discord invites have no concept of a label. We need a way to say "this
/// `discord.gg/xK3p9Q` is the link on our website" without changing the
/// invite itself. The label lives here; the invite stays a normal Discord
/// invite. Recruits never see the label — it's officer-side metadata.
///
/// ── Lifecycle ──
/// IsActive flips false when InviteAttributionService observes Discord's
/// InviteDeleted gateway event, which fires for both manual revocation
/// (officer ran /invite revoke or deleted via Discord UI) and natural
/// expiration (Discord enforced ExpiresAt or MaxUses). Rows are NEVER
/// hard-deleted — the InviteJoin.LabelSnapshot copy preserves attribution
/// for historical reports even after the source is inactive, but only
/// if the source row also stays around for the optional join → source
/// trace via InviteCode.
///
/// ── ExpiresAt and MaxUses are display-only mirrors ──
/// Discord enforces both limits authoritatively. We cache the values here
/// so /invite list can render "expires in 3d · 12/50 uses" without a
/// per-row API roundtrip. If our cached copy ever drifts from Discord's
/// reality, Discord wins — the InviteDeleted event will catch the actual
/// revocation regardless of what we have stored.
/// </summary>
public class InviteSource
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }

    /// <summary>
    /// The Discord invite code (the part after discord.gg/). Globally unique
    /// at Discord's level; we still scope the unique index to (GuildId, Code)
    /// so a future multi-guild deployment doesn't collide.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Officer-supplied label, e.g. "Website", "Facebook", "Recruiter — Smith".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// True for the guild's vanity URL (discord.gg/189th). Vanity invites
    /// don't appear in GetInvitesAsync() — they're fetched separately via
    /// GetVanityInviteAsync — so the attribution service treats them on a
    /// dedicated code path. There is at most one IsVanity=true row per guild.
    /// </summary>
    public bool IsVanity { get; set; }

    /// <summary>
    /// False once Discord deletes the underlying invite (manual revoke or
    /// natural expiration). We keep the row for historical attribution.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Discord user ID of the officer who ran /invite create or /invite assign.
    /// Null for the vanity row (system-seeded) and for any future system-created
    /// invites.
    /// </summary>
    public ulong? CreatedByDiscordId { get; set; }

    /// <summary>Username snapshot of the creator, captured at create time for log readability.</summary>
    public string CreatedByUsername { get; set; } = string.Empty;

    /// <summary>When this row was written. Distinct from DiscordCreatedAt for /invite assign cases.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When Discord created the underlying invite. May be earlier than CreatedAt
    /// if /invite assign was used to retroactively label a pre-existing invite.
    /// Null for vanity (no meaningful Discord create timestamp).
    /// </summary>
    public DateTime? DiscordCreatedAt { get; set; }

    /// <summary>
    /// Cached display copy of when Discord will auto-revoke this invite.
    /// Null = no expiration (lives forever until manually revoked or hits MaxUses).
    /// Discord is the source of truth; this is just for fast /invite list rendering.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Cached display copy of the use cap. Null = unlimited. Discord enforces.
    /// </summary>
    public int? MaxUses { get; set; }

    /// <summary>
    /// Channel the invite drops new joiners into. Null for vanity (vanity URL
    /// uses the guild's default invite channel, configured in Discord server
    /// settings, which we don't need to track).
    /// </summary>
    public ulong? ChannelId { get; set; }

    /// <summary>Set when IsActive flips false; useful for "when did this go away" queries.</summary>
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>Optional free-form note from the officer ("Q2 recruitment push", etc.). Empty if unset.</summary>
    public string Notes { get; set; } = string.Empty;
}
