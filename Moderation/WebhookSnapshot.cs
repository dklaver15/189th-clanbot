namespace ClanGuardBot.Models;

/// <summary>
/// One row per webhook the bot has seen in the guild. The
/// <see cref="Handlers.WebhookAuditService"/> updates this table on
/// every scan: webhooks present in Discord but not in the table are
/// new, rows present in the table but not in Discord were deleted,
/// rows that match by ID but differ in Name / ChannelId / ApplicationId
/// changed. The diff drives the alerts; the persistence drives the
/// audit trail.
///
/// ── Why we persist this ──
/// The Audit Log Watcher (feature #3) catches webhook creation in
/// real time, but Discord's audit log only retains 45 days and the
/// real-time alert can be missed if the bot was offline. The
/// periodic scan is the catch-everything backstop: it always sees the
/// current state, regardless of when the bot was last online. The
/// stored snapshot is also what powers <c>/webhook-audit</c>'s
/// "first seen on" column, which is the single most useful question
/// during incident response ("how long has this thing been here?").
///
/// ── Why FirstSeenUtc / LastSeenUtc and not a separate history table ──
/// Webhooks change rarely — typically only at create / delete / occasional
/// rename. A change-stream table would mostly be empty. Two timestamps
/// give us "when did we first notice this webhook" and "when did we
/// last confirm it exists" without scaling rows with scan count. Real
/// change events are also already captured by SecurityAuditRecord,
/// which the watcher writes on every diff finding — so the structured
/// timeline lives there, not here.
///
/// ── What we never store ──
/// The webhook token is the secret that lets anyone post as the
/// webhook. It MUST NOT be logged, displayed, or persisted. Only Id,
/// Name, ChannelId, ApplicationId, and Creator info land in this
/// table — all public-ish metadata.
///
/// Lives in its own file (not Entities.cs) following the convention
/// set by SecurityAuditRecord and AwolKickAuditRecord — server-
/// protection tables are self-contained operational concerns.
/// </summary>
public class WebhookSnapshot
{
    public int Id { get; set; }

    /// <summary>Discord guild (server) ID. Multi-guild-safe even though ClanGuard runs in a single guild today.</summary>
    public ulong GuildId { get; set; }

    /// <summary>
    /// Discord webhook ID. Stable across renames and channel moves —
    /// this is the diff key. The Discord API guarantees uniqueness
    /// across all webhooks ever created globally.
    /// </summary>
    public ulong WebhookId { get; set; }

    /// <summary>
    /// Channel the webhook posts to. Tracked separately so we can
    /// detect a webhook being MOVED — that's suspicious behavior worth
    /// an alert ("this webhook used to post to #test, now it posts to
    /// #announcements"). Nullable to match Discord's API (application-
    /// owned webhooks can have null ChannelId), though in practice
    /// every webhook we'll see in a guild scan has a channel.
    /// </summary>
    public ulong? ChannelId { get; set; }

    /// <summary>
    /// Display name of the webhook. Easily renamed by anyone with
    /// Manage Webhooks, so don't trust this as an identity — but
    /// rename events are themselves an alert signal (e.g. "GitHub" →
    /// "Announcement" is a strong hint someone's repurposing a
    /// webhook for spoofing).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Application that owns the webhook, if any. Set for integration
    /// webhooks (Apollo, GitHub, monitoring services). Null for
    /// user-created webhooks. This is the most robust allowlist key —
    /// app IDs are issued by Discord and can't be spoofed by renaming.
    /// </summary>
    public ulong? ApplicationId { get; set; }

    /// <summary>
    /// User who created the webhook, if Discord provided it. Null is
    /// common — Discord doesn't always return creator metadata,
    /// especially for older webhooks or app-installed ones.
    /// </summary>
    public ulong? CreatorUserId { get; set; }

    /// <summary>Display name of the creator at the time of first detection. Null when CreatorUserId is null.</summary>
    public string? CreatorUsername { get; set; }

    /// <summary>
    /// When this WebhookId first appeared in a scan. Anchored to scan
    /// time, not Discord's create timestamp — Discord doesn't expose
    /// the webhook's actual creation date through this API surface
    /// reliably. For "true" creation time, cross-reference the
    /// snowflake ID with <c>SnowflakeUtils.FromSnowflake</c>.
    /// </summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Most recent scan that confirmed this webhook still exists. Updated on every scan that sees it.</summary>
    public DateTime LastSeenUtc { get; set; }
}
