using ClanGuardBot.Data;
using Discord;
using Discord.Rest;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Shared building blocks for a MemberDeparture row, used by BOTH the live
/// path (DepartureCaptureHandler, reacting to UserLeft + AuditLogCreated) and
/// the offline-gap path (MemberRosterReconciler, diffing the persisted roster
/// against the live guild). Centralizing these keeps tenure resolution,
/// engagement counting, and kick/ban classification identical across both
/// paths — one place to change the rules.
/// </summary>
internal static class DepartureFactory
{
    /// <summary>
    /// Highest rank role the member holds, by position in the ordered
    /// RankRoles list (low → high). Returns "Guest" when no rank role is held
    /// but the Guest role is, else "None".
    /// </summary>
    public static string ResolveHighestRank(IReadOnlyCollection<string> roleNames, List<string> rankList)
    {
        var bestIdx = -1;
        string? best = null;
        foreach (var name in roleNames)
        {
            var idx = rankList.FindIndex(r => r.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (idx > bestIdx)
            {
                bestIdx = idx;
                best = rankList[idx];
            }
        }
        if (best is not null) return best;
        return roleNames.Any(n => n.Equals("Guest", StringComparison.OrdinalIgnoreCase)) ? "Guest" : "None";
    }

    /// <summary>
    /// Maps a guild audit action + actor + reason to a MemberDeparture
    /// classification. AWOL /kick-awols is the only bot KICK path; the
    /// account-age gate BANS new accounts with an "Account-age ..." reason.
    /// </summary>
    public static string ClassifyKickBan(bool actorIsBot, ActionType action, string? reason)
    {
        if (actorIsBot)
        {
            return action switch
            {
                ActionType.Kick => "KickedAwol",
                ActionType.Ban when (reason ?? string.Empty)
                    .StartsWith("Account-age", StringComparison.OrdinalIgnoreCase)
                    => "KickedAccountAge",
                ActionType.Ban => "Banned",
                _ => "Left",
            };
        }

        return action switch
        {
            ActionType.Kick => "Kicked",
            ActionType.Ban => "Banned",
            _ => "Left",
        };
    }

    /// <summary>Extracts the target user ID from a typed Kick/Ban audit payload, or null.</summary>
    public static ulong? TargetIdFrom(IAuditLogData? data) => data switch
    {
        KickAuditLogData k => k.Target?.Id,
        BanAuditLogData b => b.Target?.Id,
        _ => null,
    };

    public static double? TenureDays(DateTime? joinedAtUtc, DateTime departedAtUtc) =>
        joinedAtUtc is null ? null : Math.Round((departedAtUtc - joinedAtUtc.Value).TotalDays, 2);

    /// <summary>
    /// Best-effort JoinedAt resolution. First hit wins; the source is returned
    /// alongside so the briefing can report tenure coverage honestly.
    /// <paramref name="cachedJoinedAtUtc"/> is Discord's own join timestamp when
    /// available (live SocketGuildUser, or a value previously cached in
    /// KnownMember while the member was present) — treated as the exact source.
    /// </summary>
    public static async Task<(DateTime? JoinedAt, string Source)> ResolveJoinedAtAsync(
        BotDbContext db, ulong guildId, DateTime? cachedJoinedAtUtc, ulong userId, CancellationToken ct = default)
    {
        // 1. Discord-sourced timestamp — exact.
        if (cachedJoinedAtUtc is not null)
            return (cachedJoinedAtUtc, "DiscordCache");

        // 2. Most recent InviteJoin — exact-ish (only exists post invite-tracking).
        var invite = await db.InviteJoins
            .Where(j => j.GuildId == guildId && j.UserDiscordId == userId)
            .OrderByDescending(j => j.JoinedAt)
            .Select(j => (DateTime?)j.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (invite is not null) return (invite, "InviteJoin");

        // 3. GuestReminder.JoinedAt.
        var guestJoin = await db.GuestReminders
            .Where(g => g.GuildId == guildId && g.UserId == userId)
            .OrderBy(g => g.JoinedAt)
            .Select(g => (DateTime?)g.JoinedAt)
            .FirstOrDefaultAsync(ct);
        if (guestJoin is not null) return (guestJoin, "GuestReminder");

        // 4. Earliest RankHistory.AssignedAt — weak lower bound.
        var rankAssigned = await db.RankHistories
            .Where(r => r.GuildId == guildId && r.UserId == userId)
            .OrderBy(r => r.AssignedAt)
            .Select(r => (DateTime?)r.AssignedAt)
            .FirstOrDefaultAsync(ct);
        if (rankAssigned is not null) return (rankAssigned, "RankHistory");

        // 5. Earliest activity timestamp — lower bound.
        var firstMsg = await db.MessageEvents
            .Where(m => m.GuildId == guildId && m.UserId == userId)
            .OrderBy(m => m.Timestamp)
            .Select(m => (DateTime?)m.Timestamp)
            .FirstOrDefaultAsync(ct);
        var firstVoice = await db.VoiceSessions
            .Where(v => v.GuildId == guildId && v.UserId == userId)
            .OrderBy(v => v.JoinedAt)
            .Select(v => (DateTime?)v.JoinedAt)
            .FirstOrDefaultAsync(ct);

        var firstActivity = new[] { firstMsg, firstVoice }
            .Where(t => t is not null)
            .OrderBy(t => t!.Value)
            .FirstOrDefault();
        if (firstActivity is not null) return (firstActivity, "FirstActivity");

        return (null, "Unknown");
    }

    public static async Task<(int Messages, int Events)> GetEngagementAsync(
        BotDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
    {
        var messages = await db.MessageEvents
            .CountAsync(m => m.GuildId == guildId && m.UserId == userId, ct);
        var events = await db.EventAttendances
            .CountAsync(a => a.GuildId == guildId && a.UserId == userId, ct);
        return (messages, events);
    }

    public static async Task<string?> GetJoinSourceLabelAsync(
        BotDbContext db, ulong guildId, ulong userId, CancellationToken ct = default)
    {
        return await db.InviteJoins
            .Where(j => j.GuildId == guildId && j.UserDiscordId == userId)
            .OrderByDescending(j => j.JoinedAt)
            .Select(j => (string?)j.LabelSnapshot)
            .FirstOrDefaultAsync(ct);
    }
}
