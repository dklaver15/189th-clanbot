using System.Text.RegularExpressions;
using Discord.WebSocket;

namespace ClanGuardBot.Services;

/// <summary>
/// Rewrites Discord mention tokens (<c>&lt;#id&gt;</c>, <c>&lt;@id&gt;</c>,
/// <c>&lt;@&amp;id&gt;</c>) into readable plain text — <c>#general</c>,
/// <c>@Dan</c>, <c>@Officer</c> — for places Discord does NOT render mentions.
///
/// ── Why this exists ──
/// A native Discord poll's question and answer text are plain strings: Discord
/// renders no markdown and resolves no mentions inside them. So a question typed
/// as "…remove the messages in &lt;#123456789012345678&gt;?" posts verbatim,
/// showing members the raw snowflake instead of the channel. Resolving the token
/// to "#general" up front gets the intended reading (just without the link,
/// which a native poll structurally can't carry).
///
/// Only applied to <b>native</b> polls. Anonymous polls render through an embed,
/// where Discord resolves the real tokens into clickable links — those are left
/// exactly as typed, and so is the optional description (it rides as ordinary
/// message content, which also renders links properly).
///
/// Unresolvable ids fall back to a neutral placeholder rather than leaking a raw
/// snowflake, since a stale/deleted target is precisely the case where the raw
/// token reads worst.
/// </summary>
public static class PollMentionResolver
{
    private static readonly Regex ChannelToken = new(@"<#(\d{15,25})>", RegexOptions.Compiled);
    private static readonly Regex RoleToken    = new(@"<@&(\d{15,25})>", RegexOptions.Compiled);
    private static readonly Regex UserToken    = new(@"<@!?(\d{15,25})>", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="text"/> with every channel/role/user mention token
    /// replaced by its readable name. Null/blank input and text with no tokens are
    /// returned untouched (the common case costs three cheap regex misses).
    /// </summary>
    public static string? Resolve(DiscordSocketClient? client, ulong guildId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf('<') < 0) return text;

        var guild = client?.GetGuild(guildId);

        // Roles before users: <@&id> would otherwise never match, since the user
        // pattern's optional '!' does not cover '&' but the ordering keeps intent
        // obvious to the next reader.
        text = ChannelToken.Replace(text, m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            var name = guild?.GetChannel(id)?.Name
                    ?? (client?.GetChannel(id) as SocketGuildChannel)?.Name;
            return name is null ? "#unknown-channel" : "#" + name;
        });

        text = RoleToken.Replace(text, m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            var name = guild?.GetRole(id)?.Name;
            return name is null ? "@unknown-role" : "@" + name;
        });

        text = UserToken.Replace(text, m =>
        {
            var id = ulong.Parse(m.Groups[1].Value);
            var name = guild?.GetUser(id)?.DisplayName
                    ?? client?.GetUser(id)?.Username;
            return name is null ? "@unknown-user" : "@" + name;
        });

        return text;
    }
}
