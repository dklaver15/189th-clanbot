using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;

namespace ClanGuardBot.Services;

/// <summary>
/// Resolves the free-text a creator types in the DM wizard (which has no guild
/// autocomplete) into concrete targets: a destination channel, and an optional
/// set of ping targets (roles / users / @everyone / @here). Accepts ids, mentions,
/// channel links, and plain names so the creator can paste whatever's convenient.
/// </summary>
public static class ReminderTargets
{
    private static readonly Regex ChannelLink =
        new(@"/channels/\d+/(?<id>\d+)", RegexOptions.Compiled);
    private static readonly Regex ChannelMention =
        new(@"^<#(?<id>\d+)>$", RegexOptions.Compiled);
    private static readonly Regex RoleMention =
        new(@"^<@&(?<id>\d+)>$", RegexOptions.Compiled);
    private static readonly Regex UserMention =
        new(@"^<@!?(?<id>\d+)>$", RegexOptions.Compiled);

    /// <summary>
    /// Resolves a target text channel from an id, a &lt;#id&gt; mention, a channel
    /// link, or a (case-insensitive) channel name. Returns false with a
    /// creator-facing <paramref name="error"/> when nothing usable resolves.
    /// </summary>
    public static bool TryResolveChannel(
        SocketGuild guild, string input, out SocketTextChannel? channel, out string error)
    {
        channel = null;
        error = string.Empty;
        input = input.Trim();

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Tell me which channel to post in — paste a channel link, its id, or type its name (e.g. `#announcements`).";
            return false;
        }

        // 1) Try to pull an explicit id from a link / mention / raw digits.
        ulong id = 0;
        var linkMatch = ChannelLink.Match(input);
        var mentionMatch = ChannelMention.Match(input);
        if (linkMatch.Success) ulong.TryParse(linkMatch.Groups["id"].Value, out id);
        else if (mentionMatch.Success) ulong.TryParse(mentionMatch.Groups["id"].Value, out id);
        else if (ulong.TryParse(input, out var raw)) id = raw;

        if (id != 0)
        {
            channel = guild.GetTextChannel(id);
            if (channel is null)
            {
                error = "I couldn't find a text channel with that id in this server. Paste the channel link or type its name instead.";
                return false;
            }
            return true;
        }

        // 2) Fall back to a name match (strip a leading '#').
        var name = input.TrimStart('#').Trim();
        var matches = guild.TextChannels
            .Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
        {
            channel = matches[0];
            return true;
        }
        if (matches.Count == 0)
        {
            error = $"I couldn't find a text channel named **{name}** here. Paste the channel link (right-click → Copy Link) or its id.";
            return false;
        }

        error = $"There are several channels named **{name}**. Paste the exact channel link or its id so I pick the right one.";
        return false;
    }

    public sealed class PingResult
    {
        public List<ulong> RoleIds { get; } = new();
        public List<ulong> UserIds { get; } = new();
        public bool Everyone { get; set; }
        public bool Here { get; set; }

        /// <summary>Tokens that matched no role/user — surfaced so the creator can fix them.</summary>
        public List<string> Unresolved { get; } = new();

        public bool Any => RoleIds.Count > 0 || UserIds.Count > 0 || Everyone || Here;
    }

    /// <summary>
    /// Parses a whitespace/comma-separated list of ping targets. Each token may be
    /// <c>@everyone</c> / <c>@here</c>, a role/user mention, a raw id, a role name,
    /// or a member name. Role matches win over user matches on a bare name. Never
    /// throws; unmatched tokens land in <see cref="PingResult.Unresolved"/>.
    /// </summary>
    public static PingResult ParsePings(SocketGuild guild, string input)
    {
        var result = new PingResult();
        var tokens = input.Split(new[] { ',', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var lower = token.TrimStart('@').ToLowerInvariant();
            if (lower is "everyone") { result.Everyone = true; continue; }
            if (lower is "here")     { result.Here = true; continue; }

            var roleMention = RoleMention.Match(token);
            if (roleMention.Success && ulong.TryParse(roleMention.Groups["id"].Value, out var rid))
            {
                AddDistinct(result.RoleIds, rid);
                continue;
            }

            var userMention = UserMention.Match(token);
            if (userMention.Success && ulong.TryParse(userMention.Groups["id"].Value, out var uid))
            {
                AddDistinct(result.UserIds, uid);
                continue;
            }

            if (ulong.TryParse(token, out var idNum))
            {
                if (guild.GetRole(idNum) is not null) { AddDistinct(result.RoleIds, idNum); continue; }
                if (guild.GetUser(idNum) is not null) { AddDistinct(result.UserIds, idNum); continue; }
                result.Unresolved.Add(token);
                continue;
            }

            // Bare name — prefer a role, then a member (by nickname/username).
            var name = token.TrimStart('@').Trim();
            var role = guild.Roles.FirstOrDefault(r =>
                !r.IsEveryone && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (role is not null) { AddDistinct(result.RoleIds, role.Id); continue; }

            var member = guild.Users.FirstOrDefault(u =>
                string.Equals(u.DisplayName, name, StringComparison.OrdinalIgnoreCase)
             || string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase));
            if (member is not null) { AddDistinct(result.UserIds, member.Id); continue; }

            result.Unresolved.Add(token);
        }

        return result;
    }

    private static void AddDistinct(List<ulong> list, ulong id)
    {
        if (!list.Contains(id)) list.Add(id);
    }

    /// <summary>
    /// Builds the message-content mention prefix (roles, users, then @here/@everyone)
    /// that goes ABOVE the embed so the pings actually notify. Returns empty when
    /// there are no pings.
    /// </summary>
    public static string BuildMentionContent(
        IEnumerable<ulong> roleIds, IEnumerable<ulong> userIds, bool everyone, bool here)
    {
        var parts = new List<string>();
        parts.AddRange(roleIds.Select(id => $"<@&{id}>"));
        parts.AddRange(userIds.Select(id => $"<@{id}>"));
        if (here) parts.Add("@here");
        if (everyone) parts.Add("@everyone");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Builds the <see cref="AllowedMentions"/> that whitelists EXACTLY the intended
    /// targets, so nothing incidental in the text can ping. Explicit role/user id
    /// lists are used (never the Roles/Users type flags, which would conflict); the
    /// Everyone flag is added only when @everyone or @here was requested.
    /// </summary>
    public static AllowedMentions BuildAllowedMentions(
        IReadOnlyCollection<ulong> roleIds, IReadOnlyCollection<ulong> userIds, bool everyone, bool here)
    {
        var allowed = new AllowedMentions
        {
            RoleIds = roleIds.Count > 0 ? roleIds.ToList() : null,
            UserIds = userIds.Count > 0 ? userIds.ToList() : null,
        };
        allowed.AllowedTypes = (everyone || here) ? AllowedMentionTypes.Everyone : AllowedMentionTypes.None;
        return allowed;
    }
}
