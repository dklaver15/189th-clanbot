namespace ClanGuardBot.Models;

/// <summary>
/// One row per support ticket opened via the ticket panel (the persistent
/// category select menu posted to the public tickets channel by
/// /ticket-panel).
///
/// ── Lifecycle ──
/// 1. Member picks a category from the panel select menu. A modal pops with
///    Subject + Details.
/// 2. On submit, a row is inserted with Status = Open. For an identified
///    category the bot creates a PRIVATE THREAD under the ticket-center
///    channel, adds the member + the routed role, and posts the ticket embed
///    with claim/close/priority controls. For an ANONYMOUS category the
///    thread is created staff-only (the member is NOT added) and the embed
///    shows an anonymized handle instead of the opener's identity.
/// 3. HQ works the ticket in-thread. Claim assigns a handler; priority can be
///    bumped; close compiles a transcript to the log channel and flips
///    Status = Closed.
///
/// ── Anonymity ──
/// IsAnonymous tickets store the real OpenerUserId (so the audit trail is
/// intact and a designated role can unmask if ever necessary) but never
/// surface it in the thread. AnonHandle is the short public label shown to
/// staff (e.g. "Anonymous #a3f9").
///
/// ── Time-off ──
/// The AWOL / time-off category carries optional leave-window fields. When an
/// officer approves a time-off ticket the bot assigns the Reserve role (which
/// is AWOL-exempt) for the window and removes it afterward. ReserveAssigned
/// tracks whether the grant is currently active so the removal sweep is
/// restart-safe.
///
/// ── Audit ──
/// Rows are never deleted — closed tickets stay for the record and feed the
/// transcript in the log channel.
/// </summary>
public class SupportTicket
{
    public int Id { get; set; }

    /// <summary>Guild the ticket was opened in.</summary>
    public ulong GuildId { get; set; }

    /// <summary>
    /// Category key (matches a TicketCategoryDef.Key from
    /// BotConfig.GetTicketCategories) — e.g. "report", "recruit", "awol".
    /// </summary>
    public string CategoryKey { get; set; } = string.Empty;

    /// <summary>Human-readable category label, snapshotted at open time.</summary>
    public string CategoryLabel { get; set; } = string.Empty;

    /// <summary>Discord user ID of the member who opened the ticket.</summary>
    public ulong OpenerUserId { get; set; }

    /// <summary>
    /// Snapshot of the opener's display name at open time. NOT shown for
    /// anonymous tickets — kept for the audit trail / unmask only.
    /// </summary>
    public string OpenerDisplayName { get; set; } = string.Empty;

    /// <summary>Subject line from the creation modal.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Body / details from the creation modal.</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>True for tickets opened under an anonymity-enabled category.</summary>
    public bool IsAnonymous { get; set; }

    /// <summary>
    /// Short public handle for anonymous tickets (e.g. "Anonymous #a3f9").
    /// Null for identified tickets.
    /// </summary>
    public string? AnonHandle { get; set; }

    /// <summary>Discord role ID pinged/added when the ticket was routed.</summary>
    public ulong RoutedRoleId { get; set; }

    /// <summary>Thread the ticket conversation lives in.</summary>
    public ulong ThreadId { get; set; }

    /// <summary>Message ID of the ticket embed (carries the control buttons).</summary>
    public ulong ControlMessageId { get; set; }

    public SupportTicketStatus   Status   { get; set; } = SupportTicketStatus.Open;
    public SupportTicketPriority Priority { get; set; } = SupportTicketPriority.Normal;

    public DateTime CreatedUtc      { get; set; }
    public DateTime LastActivityUtc { get; set; }

    // ── Claim ────────────────────────────────────────────────────────
    public ulong?  ClaimedByUserId   { get; set; }
    public string? ClaimedByUsername { get; set; }
    public DateTime? ClaimedAtUtc    { get; set; }

    // ── Close ────────────────────────────────────────────────────────
    public DateTime? ClosedUtc         { get; set; }
    public ulong?    ClosedByUserId    { get; set; }
    public string?   ClosedByUsername  { get; set; }
    public string?   Resolution        { get; set; }

    // ── Escalation (background sweep, later phase) ────────────────────
    /// <summary>
    /// How many times the escalation sweep has bumped this ticket. Persisted
    /// so a restart between sweeps can't double-escalate.
    /// </summary>
    public int EscalationLevel { get; set; }

    // ── Time-off (AWOL category) ─────────────────────────────────────
    public DateTime? LeaveStartUtc  { get; set; }
    public DateTime? LeaveEndUtc    { get; set; }

    /// <summary>
    /// True while an approved leave is being managed by the maintenance sweep —
    /// from approval until the end date passes. Combined with ReserveAssigned it
    /// encodes the lifecycle: LeaveScheduled=true & ReserveAssigned=false =
    /// "approved, waiting for the start date"; both true = "leave active";
    /// LeaveScheduled=false = "no leave, or finished".
    /// </summary>
    public bool LeaveScheduled { get; set; }

    /// <summary>True while the Reserve role is currently applied for this leave.</summary>
    public bool ReserveAssigned { get; set; }
}

/// <summary>
/// One row per message exchanged on a ticket. Feeds the close-time transcript
/// posted to the log channel, and (for anonymous tickets, a later phase) the
/// DM relay between the reporter and staff. Direction distinguishes who sent
/// it so the transcript reads correctly even when the opener's identity is
/// masked in the thread.
/// </summary>
public class SupportTicketMessage
{
    public int Id { get; set; }

    public int TicketId { get; set; }

    /// <summary>Discord user ID of the author.</summary>
    public ulong AuthorUserId { get; set; }

    /// <summary>Author display name snapshot (for the transcript).</summary>
    public string AuthorDisplayName { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime SentUtc { get; set; }

    /// <summary>Who the message is from, relative to the ticket.</summary>
    public SupportTicketMessageDirection Direction { get; set; }
}

/// <summary>Lifecycle state for a SupportTicket.</summary>
public enum SupportTicketStatus
{
    Open       = 0,
    InProgress = 1,   // claimed / actively being worked
    Waiting    = 2,   // waiting on the opener
    Closed     = 3,
}

/// <summary>
/// Priority for a SupportTicket. Values are ordered low → high so the
/// escalation sweep can bump by incrementing. Drives the embed color.
/// </summary>
public enum SupportTicketPriority
{
    Low    = 0,
    Normal = 1,
    High   = 2,
    Urgent = 3,
}

/// <summary>Direction of a SupportTicketMessage relative to the ticket.</summary>
public enum SupportTicketMessageDirection
{
    /// <summary>From the member who opened the ticket.</summary>
    FromOpener = 0,
    /// <summary>From HQ / staff.</summary>
    FromStaff  = 1,
    /// <summary>System / bot note (open, claim, priority change, close).</summary>
    System     = 2,
}

/// <summary>
/// Parsed definition of one ticket category, produced by
/// BotConfig.GetTicketCategories from the TicketCategoriesCsv config string.
/// Immutable value object — not persisted (the ticket snapshots the bits it
/// needs at open time).
/// </summary>
public sealed record TicketCategoryDef(
    string Key,
    string Label,
    string Emoji,
    ulong RoutedRoleId,
    bool AnonymousAllowed,
    SupportTicketPriority DefaultPriority);
