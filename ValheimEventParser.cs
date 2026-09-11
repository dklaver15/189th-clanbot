namespace ClanGuardBot.Services;

/// <summary>What happened on the Valheim server, as reported by DiscordConnector.</summary>
public enum ValheimEventKind
{
    Join,
    Leave,
    Death,

    /// <summary>Server finished loading and is accepting connections.</summary>
    ServerStart,

    /// <summary>Server finished shutting down.</summary>
    ServerStop,
}

/// <summary>
/// One parsed <c>DCX1|…</c> line.
/// </summary>
/// <param name="Kind">Which event. Server events carry no player.</param>
/// <param name="PlayerId">
/// DiscordConnector's <c>%PLAYER_ID%</c> — a platform-prefixed identity like
/// "STEAM_76561198…" (Xbox and Game Pass use their own prefixes). Empty when the
/// variable didn't substitute, which is what <see cref="HasStableId"/> reports.
/// </param>
/// <param name="PlayerName">In-game character name. Freely changeable, so never an identity.</param>
/// <param name="OnlineCount">Players online per the server, or null when not supplied.</param>
public sealed record ValheimEvent(
    ValheimEventKind Kind,
    string PlayerId,
    string PlayerName,
    int? OnlineCount)
{
    /// <summary>True when a real platform id came through, i.e. sessions can key on identity.</summary>
    public bool HasStableId => PlayerId.Length > 0;

    /// <summary>
    /// What sessions are keyed on: the platform id when available, else the
    /// character name.
    ///
    /// <para>The fallback is a genuine downgrade, not an equivalent — two players
    /// sharing a name collapse into one, and a rename starts a new identity. It
    /// exists so an unsupported <c>%PLAYER_ID%</c> degrades the feature instead of
    /// breaking it; <see cref="HasStableId"/> is how callers tell which world
    /// they're in.</para>
    /// </summary>
    public string IdentityKey => HasStableId ? PlayerId : PlayerName;

    public bool IsPlayerEvent => Kind is ValheimEventKind.Join or ValheimEventKind.Leave or ValheimEventKind.Death;
}

/// <summary>
/// Parses the machine-readable lines DiscordConnector is configured to emit:
///
/// <code>DCX1|join|STEAM_76561198…|3|Brandt</code>
///
/// ── Why a custom format rather than parsing the default prose ──
/// DiscordConnector's message templates are fully configurable, so rather than
/// reverse-engineering "%PLAYER_NAME% has joined." — which an admin could change at
/// any time, and which carries no player id at all — the templates are set to a
/// format defined here. Parsing becomes exact instead of heuristic.
///
/// ── Why this is written defensively ──
/// The lines arrive in a shared Discord channel (#bot-stuff), NOT a locked-down
/// one. Anyone who can type there can type something shaped like an event, and a
/// forged "leave" or "join" would corrupt playtime totals and the leaderboard. So:
///
///   • <see cref="ValheimEventIngestHandler"/> requires the message to come from a
///     WEBHOOK, which ordinary members cannot impersonate, and optionally from one
///     specific webhook id.
///   • Everything here is a whitelist: exact prefix, exact field count, known event
///     keyword, bounded lengths, numeric count. Anything else returns false.
///
/// Neither check alone is sufficient — the webhook check is the real barrier, and
/// this one keeps a malformed or hostile payload from reaching the database even if
/// something upstream goes wrong.
///
/// ── Shouts deliberately cannot reach here ──
/// Shout messages contain player-authored free text and are routed to the SECONDARY
/// webhook (a different channel) by the mod's event filter, so a player cannot shout
/// a fake event line into the ingest channel. That is a structural guarantee, not a
/// filter this parser applies.
/// </summary>
public static class ValheimEventParser
{
    /// <summary>
    /// Format marker and version. Bumping this on a breaking format change lets old
    /// and new lines coexist during a rollout instead of being silently misread.
    /// </summary>
    public const string Prefix = "DCX1";

    /// <summary>Fields: prefix, kind, id, count, name.</summary>
    private const int FieldCount = 5;

    /// <summary>
    /// Cap on id and name length. Real values are far shorter; this just stops an
    /// absurd payload from reaching a database column.
    /// </summary>
    private const int MaxFieldLength = 64;

    /// <summary>Sanity bound on the player count. Valheim caps far below this.</summary>
    private const int MaxOnlineCount = 1000;

    /// <summary>
    /// Attempts to parse one message. Returns false for anything that isn't an
    /// exactly-formed event line — including ordinary chat in the channel, which is
    /// the common case and not an error.
    /// </summary>
    public static bool TryParse(string? content, out ValheimEvent? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(content)) return false;

        // Discord may deliver a trailing newline; the mod never sends a multi-line
        // event, so anything beyond the first line means this isn't one of ours.
        var line = content.Trim();
        if (line.Contains('\n') || line.Contains('\r')) return false;

        // Cheap reject before splitting — the overwhelming majority of messages in
        // a shared channel are ordinary chat.
        if (!line.StartsWith(Prefix + "|", StringComparison.Ordinal)) return false;

        // Limit of 5 means the name field absorbs any '|' a character name contains
        // instead of shifting every subsequent column. That is why name is last.
        var parts = line.Split('|', FieldCount);
        if (parts.Length != FieldCount) return false;

        if (!TryParseKind(parts[1], out var kind)) return false;

        var id = Clean(parts[2]);
        var name = Clean(parts[4]);
        var count = ParseCount(parts[3]);

        // A player event with no usable identity at all is unusable: we could
        // neither open a session nor match it to an existing one.
        if (kind is ValheimEventKind.Join or ValheimEventKind.Leave or ValheimEventKind.Death
            && id.Length == 0 && name.Length == 0)
        {
            return false;
        }

        result = new ValheimEvent(kind, id, name, count);
        return true;
    }

    private static bool TryParseKind(string raw, out ValheimEventKind kind)
    {
        switch (raw)
        {
            case "join": kind = ValheimEventKind.Join; return true;
            case "leave": kind = ValheimEventKind.Leave; return true;
            case "death": kind = ValheimEventKind.Death; return true;
            case "server_start": kind = ValheimEventKind.ServerStart; return true;
            case "server_stop": kind = ValheimEventKind.ServerStop; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// Normalizes one field to either a usable value or the empty string.
    ///
    /// <para>Three things collapse to empty, and all three mean "absent": the
    /// literal placeholder for unused fields ("-"), whitespace, and an
    /// UNSUBSTITUTED template variable. That last one is the important case — if
    /// <c>%PLAYER_ID%</c> isn't supported by the installed mod version, the line
    /// arrives with the literal text "%PLAYER_ID%" in it, and storing that as an
    /// identity would silently merge every player on the server into one.</para>
    /// </summary>
    private static string Clean(string raw)
    {
        var value = raw.Trim();

        if (value.Length == 0 || value == "-") return string.Empty;

        // An unsubstituted variable, e.g. "%PLAYER_ID%".
        if (value.Length >= 2 && value[0] == '%' && value[^1] == '%') return string.Empty;

        return value.Length > MaxFieldLength ? value[..MaxFieldLength] : value;
    }

    private static int? ParseCount(string raw)
    {
        var value = Clean(raw);
        if (value.Length == 0) return null;
        if (!int.TryParse(value, out var count)) return null;
        return count is >= 0 and <= MaxOnlineCount ? count : null;
    }
}
