using System.Text;

namespace ClanGuardBot.Services;

/// <summary>
/// Stable content hash over the parser-relevant fields of an Apollo event.
///
/// "Content identity" here means: would two messages, if both parsed, describe
/// the same logical event? Apollo's /sort deletes an event's message and
/// re-posts an identical one under a new message ID, so the message ID is NOT
/// a stable identity — but the parsed content (title, start, end, description,
/// organizer) is. This hash is how the pipeline recognizes a sort-driven
/// re-post as "the same event" and re-binds rather than cancel-then-recreate.
///
/// Deliberately excludes RSVP/attendee data: Apollo re-renders the embed on
/// every signup, and we don't want those re-renders to change the identity.
/// The parser already drops attendee fields, so feeding it the parsed struct
/// (not the raw embed) keeps the hash stable across RSVP churn.
///
/// FNV-1a, 64-bit. Not cryptographic — we only need cheap, deterministic
/// equality. Formerly private to ApolloMessageParserWorker; lifted here so
/// ApolloReconciliationService can compute the same value against live
/// channel messages.
/// </summary>
public static class ApolloContentHash
{
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime  = 1099511628211;

    public static string Compute(ApolloEmbedParser.ParsedApolloEvent parsed)
    {
        var input = string.Join("|",
            parsed.Title,
            parsed.StartUtc.Ticks,
            parsed.EndUtc.Ticks,
            parsed.Description ?? "",
            parsed.OrganizerId?.ToString() ?? "");

        ulong hash = FnvOffset;
        foreach (var b in Encoding.UTF8.GetBytes(input))
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash.ToString("x16");
    }
}
