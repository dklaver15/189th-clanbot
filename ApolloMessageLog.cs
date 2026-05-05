namespace ClanGuardBot.Models;

/// <summary>
/// Lossless append-only log of every Apollo message Discord delivers to us.
///
/// One row per (DiscordMessageId, RevisionNumber) pair:
///   • RevisionNumber=1, EventType=Created   → original post seen via MessageReceived
///   • RevisionNumber=2+, EventType=Updated  → each subsequent edit seen via MessageUpdated
///   • RevisionNumber=N+1, EventType=Deleted → terminal tombstone seen via MessageDeleted
///
/// Phase 1 of the Apollo sync rework. Captured BEFORE any parsing or GCal calls
/// so that downstream stages (parser worker in Phase 2, reconciler in Phase 4,
/// replay command in Phase 6) can be replayed against the raw payload without
/// needing to refetch from Discord.
///
/// PayloadJson holds an ApolloMessageSnapshot serialized to JSON via
/// System.Text.Json. Null only for Deleted rows, since the message body is no
/// longer available at deletion time.
/// </summary>
public class ApolloMessageLog
{
    public long Id { get; set; }

    /// <summary>Discord message snowflake. Stable identity across all revisions.</summary>
    public ulong DiscordMessageId { get; set; }

    public ulong ChannelId { get; set; }

    public ulong GuildId { get; set; }

    /// <summary>Apollo's bot user ID. Stored for audit / verification.</summary>
    public ulong AuthorId { get; set; }

    /// <summary>1 for the original Created row, 2+ for each Updated row, terminal value for Deleted.</summary>
    public int RevisionNumber { get; set; }

    public ApolloMessageEventType EventType { get; set; }

    /// <summary>
    /// JSON-serialized ApolloMessageSnapshot. Null for Deleted rows.
    /// </summary>
    public string? PayloadJson { get; set; }

    public DateTime CapturedAt { get; set; }

    /// <summary>Set by the Phase 2 parser worker once turned into an ApolloEvent. Null = unprocessed.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Last error from the parser, if any. Null when unprocessed or successfully processed.</summary>
    public string? ParseError { get; set; }
}

public enum ApolloMessageEventType
{
    Created = 1,
    Updated = 2,
    Deleted = 3,
}
