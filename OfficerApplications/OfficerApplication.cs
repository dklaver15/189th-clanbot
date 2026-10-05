namespace ClanGuardBot.Models;

/// <summary>
/// One row per officer application submitted via the /apply flow that
/// originates from the persistent button in the officer-applications
/// instructions channel.
///
/// ── Lifecycle ──
/// 1. Member clicks the "Apply for Officer" button in the instructions
///    channel. A modal pops with three questions.
/// 2. On submit, a row is inserted with Status = Pending and the dossier
///    embed is posted to the HQ-only channel with @HQ pinged.
/// 3. (Phase 3) HQ reviews via approve/deny buttons on the dossier embed;
///    Status flips to Approved or Denied, ReviewedAt/ReviewedByUserId are
///    set, and an optional ReviewNotes string is recorded.
///
/// ── Duplicate handling ──
/// Phase 1 rule: if a Pending row already exists for a (GuildId, UserId),
/// a new modal submission is REJECTED with an ephemeral message telling
/// the applicant their previous application is still under review.
/// Reapplication is allowed only after the current Pending row has been
/// resolved (Approved or Denied).
///
/// ── Audit ──
/// Rows are never deleted. Denied applicants can reapply (a new row is
/// inserted on the next submit) but the historical denial is preserved
/// for the dossier's "previous applications" section in a future phase.
/// </summary>
public class OfficerApplication
{
    public int Id { get; set; }

    /// <summary>Guild the application was submitted in.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Discord user ID of the applicant.</summary>
    public ulong UserId { get; set; }

    /// <summary>
    /// Snapshot of the applicant's display name at submission time. Useful
    /// for audit if they're kicked or change their nickname between submit
    /// and review.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Submitted answers — preserved verbatim.</summary>
    public string Q1AreaOfInterest { get; set; } = string.Empty;
    public string Q2Ideas           { get; set; } = string.Empty;
    public string Q3Conflict        { get; set; } = string.Empty;

    /// <summary>UTC timestamp the application was submitted.</summary>
    public DateTime SubmittedAt { get; set; }

    /// <summary>Lifecycle state.</summary>
    public OfficerApplicationStatus Status { get; set; } = OfficerApplicationStatus.Pending;

    // ── Review fields (Phase 3) ──────────────────────────────────────
    // Nullable; populated when an HQ member presses Approve/Deny on the
    // dossier embed.

    public DateTime? ReviewedAt          { get; set; }
    public ulong?    ReviewedByUserId    { get; set; }
    public string?   ReviewedByUsername  { get; set; }
    public string?   ReviewNotes         { get; set; }

    /// <summary>
    /// Discord message ID of the dossier embed posted to the HQ channel.
    /// Stored so Phase 3's approve/deny buttons can locate the original
    /// embed to edit its color/status without scanning recent history.
    /// Nullable because the row is written before the post completes.
    /// </summary>
    public ulong? DossierMessageId { get; set; }
}

/// <summary>
/// Lifecycle state for an OfficerApplication.
/// </summary>
public enum OfficerApplicationStatus
{
    Pending  = 0,
    Approved = 1,
    Denied   = 2,
}
