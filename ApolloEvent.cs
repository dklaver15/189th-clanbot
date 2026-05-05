namespace ClanGuardBot.Models;

/// <summary>
/// Phase 2 staging table for the new Apollo pipeline.
///
/// Written by ApolloMessageParserWorker (which reads ApolloMessageLog rows and
/// calls ApolloEmbedParser against reconstructed embeds). Does NOT replace
/// CalendarEvent yet — Phase 2's purpose is dual-running the new parser path
/// alongside the live ApolloEventHandler so we can compare outputs before
/// cutting over.
///
/// One row per DiscordMessageId. Updated in place when a new revision arrives.
/// On Deleted events, Status is set to Cancelled (the row is preserved for
/// audit). The Phase 3 cutover will retire this table and have the worker
/// write directly to CalendarEvent instead.
///
/// ── Verification queries ──
/// During Phase 2 dual-run, the value of this table is "show me where the new
/// parser disagrees with the old inline path":
///
///   SELECT ae.DiscordMessageId, ae.ParsedTitle, ce.Title,
///          ae.ParsedStartUtc, ce.StartUtc,
///          ae.ParsedEndUtc, ce.EndUtc
///   FROM ApolloEvents ae
///   JOIN CalendarEvents ce ON ce.DiscordMessageId = ae.DiscordMessageId
///   WHERE ae.ParsedTitle    != ce.Title
///      OR ae.ParsedStartUtc != ce.StartUtc
///      OR ae.ParsedEndUtc   != ce.EndUtc;
///
/// Empty result over a week of operation = parser worker is producing
/// equivalent output to the live path; safe to proceed to Phase 3 cutover.
/// </summary>
public class ApolloEvent
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Discord message ID. One row per message, regardless of how many revisions arrived.</summary>
    public ulong DiscordMessageId { get; set; }

    public string ParsedTitle { get; set; } = string.Empty;
    public DateTime ParsedStartUtc { get; set; }
    public DateTime ParsedEndUtc { get; set; }
    public string? ParsedDescription { get; set; }
    public ulong? ParsedOrganizerId { get; set; }
    public string? ParsedOrganizerName { get; set; }

    public ApolloEventStatus Status { get; set; } = ApolloEventStatus.Active;

    /// <summary>
    /// FNV-1a hash of the parsed values (Title|Start|End|Description|OrganizerId).
    /// Cheap "has anything actually changed?" check that lets the worker skip
    /// no-op revisions (Apollo re-renders the embed on every RSVP). Phase 3
    /// uses this to suppress redundant GCal updates.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTime ParsedAt { get; set; }

    /// <summary>Stamp set when Status flips to Cancelled.</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>
    /// ApolloMessageLog.Id of the revision that produced the current parsed
    /// values. Lets us trace any row in this table back to the exact captured
    /// payload it came from — useful when investigating "why did the parser
    /// produce this?" without re-running the parser.
    /// </summary>
    public long SourceLogId { get; set; }
}

public enum ApolloEventStatus
{
    Active = 1,
    Cancelled = 2,
}
