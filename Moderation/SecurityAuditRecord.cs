namespace ClanGuardBot.Models;

/// <summary>
/// Append-only audit log for every action taken by the server-protection
/// feature set (account-age gate, invite-link filter, audit-log watcher,
/// nickname-impersonation check, webhook audit, token-grabber scanner).
/// One row per discrete event a feature acts on, regardless of whether the
/// action was enforcement, an alert, or a no-op skip.
///
/// ── Why this exists ──
/// The Discord embed posted to #security-alerts is the live notification
/// surface, but it's not queryable — embeds scroll off and channels can be
/// purged. Discord's own audit log only retains 45 days and doesn't cover
/// "alert-only" outcomes (no Discord-side action means no Discord audit
/// entry). This table is the durable record for forensics and for any
/// future leadership-facing summary like "what did the gate catch this
/// month" or "has this user tripped any security feature before".
///
/// ── Shared across all server-protection features ──
/// Each feature stamps Feature with its own identifier and packs feature-
/// specific context into Details. Schema is intentionally loose (string
/// Action / Details rather than enums) so that adding a feature doesn't
/// require a migration — just a new constant in the writing handler.
///
/// Lives in its own file (not Entities.cs) following the convention set
/// by AwolKickAuditRecord — audit tables are self-contained operational
/// concerns and easier to reason about in isolation.
/// </summary>
public class SecurityAuditRecord
{
    public int Id { get; set; }

    /// <summary>Discord guild (server) ID.</summary>
    public ulong GuildId { get; set; }

    /// <summary>
    /// Which server-protection feature wrote this row. Stable string identifier
    /// — the source of truth for valid values is the writing handler. Known
    /// values today:
    ///   "AccountAgeGate"  — feature #1, hooks UserJoined and gates by account age.
    /// Future:
    ///   "InviteLinkFilter", "AuditLogWatcher", "NicknameImpersonation",
    ///   "WebhookAudit", "TokenGrabberScanner".
    /// Stored as string rather than enum so adding a feature is purely additive
    /// in code and never requires a DB migration.
    /// </summary>
    public string Feature { get; set; } = string.Empty;

    /// <summary>
    /// Outcome of the feature's processing. Feature-specific; documented in
    /// the handler that writes the row. For AccountAgeGate the valid values
    /// mirror the AccountAgeGateOutcome enum: "Alerted", "Kicked",
    /// "UserAlreadyLeft", "KickSkippedHierarchy", "KickFailed".
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Discord user the action concerns. Null for non-user events — e.g. a
    /// future WebhookAudit row describing an unowned webhook would carry no
    /// UserId. Mirrored by Username / DisplayName, which are also null in
    /// that case.
    /// </summary>
    public ulong? UserId { get; set; }

    /// <summary>Discord username at time of action (e.g. "gravestarr"). Null when UserId is null.</summary>
    public string? Username { get; set; }

    /// <summary>Server display name / nickname at time of action (e.g. "SSG.GRAVESTARR"). Null when UserId is null or the member had no nickname.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Channel where the relevant content was posted, when applicable. Used
    /// by message-scoped features like the invite-link filter and token-
    /// grabber scanner. Null for join-time features like AccountAgeGate that
    /// fire before the member has interacted with any channel.
    /// </summary>
    public ulong? ChannelId { get; set; }

    /// <summary>
    /// Free-form context for the row. Typically a short human-readable
    /// phrase summarising the trigger ("Account age 1.4d, threshold 3d") or
    /// a small JSON blob for richer context (e.g. role-diff for a future
    /// permission-escalation alert). Kept loose so each feature can encode
    /// what's useful without dragging the schema along.
    /// </summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this row was written.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Error message when the feature's action failed at the Discord API
    /// layer (e.g. 403 on a kick). Null on clean outcomes including
    /// intentional "skipped" outcomes — those are not failures, they're
    /// recorded reasons.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
