namespace ClanGuardBot.Models;

/// <summary>
/// Audit row written for every member processed by /kick-awols — whether they
/// were actually kicked, skipped, or failed. One row per member per command
/// invocation. Discord's own audit log only retains 45 days and doesn't include
/// skipped/failed outcomes, so this is the source of truth for AWOL kick history.
///
/// Lives in its own file (not Entities.cs) because the kick audit trail is a
/// self-contained operational concern that's easy to reason about in isolation.
/// </summary>
public class AwolKickAuditRecord
{
    public int Id { get; set; }

    /// <summary>Discord guild (server) ID.</summary>
    public ulong GuildId { get; set; }

    /// <summary>Discord user ID of the member processed.</summary>
    public ulong UserId { get; set; }

    /// <summary>Discord username (e.g. "gravestarr") at time of processing.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Server display name / nickname (e.g. "SSG.GRAVESTARR") at time of processing.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of role names the member held at time of processing
    /// (excluding @everyone). Useful for forensics — e.g. confirming the member
    /// did not have Reserve when they were kicked, or identifying which platoon
    /// they were in for offboarding follow-up.
    /// </summary>
    public string RolesAtKick { get; set; } = string.Empty;

    /// <summary>UTC timestamp when this row was written.</summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Discord user ID of the officer who ran /kick-awols.</summary>
    public ulong InvokerId { get; set; }

    /// <summary>Discord username of the officer who ran /kick-awols.</summary>
    public string InvokerName { get; set; } = string.Empty;

    /// <summary>True if the run was a dry-run (no actual kicks happened).</summary>
    public bool WasDryRun { get; set; }

    /// <summary>
    /// Outcome of processing this member. One of:
    ///   "Kicked"             — successfully removed from the server
    ///   "DryRunKicked"       — would have been kicked, but dry-run mode
    ///   "SkippedReserve"     — skipped because they had the Reserve role
    ///   "SkippedHierarchy"   — bot's role wasn't above the member's top role
    ///   "SkippedProtected"   — at/above protected min rank (leadership safety)
    ///   "Failed"             — Discord API call threw; see ErrorMessage
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The reason string passed to Discord's audit log on the kick.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>If Outcome == "Failed", the exception message. Null otherwise.</summary>
    public string? ErrorMessage { get; set; }
}