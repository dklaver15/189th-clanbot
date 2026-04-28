using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Shared promotion mechanics used by both the /promote slash command
/// and the nightly AutoPromotionService. Handles role swap, nickname
/// update, and congratulatory announcements. Contains no slash-command
/// flow specifics so it can be called from any context.
/// </summary>
public class PromotionService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PromotionService> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Rank progression chain. Key = target rank shorthand (lowercase),
    /// value = (previous rank role to remove, new rank role to add).
    /// Ranks above CPL are included for manual /promote use only;
    /// AutoPromotionService only progresses RCT → CPL.
    /// </summary>
    public static readonly Dictionary<string, (string OldRole, string NewRole)> RankMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "pvt", ("RCT", "PVT") },
            { "pfc", ("PVT", "PFC") },
            { "spc", ("PFC", "SPC") },
            { "cpl", ("SPC", "CPL") },
            { "sgt", ("CPL", "SGT") },
            { "ssg", ("SGT", "SSG") },
            { "sfc", ("SSG", "SFC") },
            { "msg", ("SFC", "MSG") },
            { "1sg", ("MSG", "1SG") },
            { "sgm", ("1SG", "SGM") },
            { "csm", ("SGM", "CSM") },
            { "sma", ("CSM", "SMA") },
        };

    /// <summary>SMA is a special case: can be promoted from either SGM or CSM.</summary>
    public static readonly HashSet<string> SmaSourceRoles =
        new(StringComparer.OrdinalIgnoreCase) { "SGM", "CSM" };

    /// <summary>
    /// Full rank names for the congratulatory blurb.
    /// Short ranks (RCT/PVT/etc.) are Discord role names; full names read nicer in chat.
    /// </summary>
    private static readonly Dictionary<string, string> RankFullNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "RCT", "Recruit" },
            { "PVT", "Private" },
            { "PFC", "Private First Class" },
            { "SPC", "Specialist" },
            { "CPL", "Corporal" },
            { "SGT", "Sergeant" },
            { "SSG", "Staff Sergeant" },
            { "SFC", "Sergeant First Class" },
            { "MSG", "Master Sergeant" },
            { "1SG", "First Sergeant" },
            { "SGM", "Sergeant Major" },
            { "CSM", "Command Sergeant Major" },
            { "SMA", "Sergeant Major of the Army" },
        };

    /// <summary>
    /// Blurb templates for the auto-promotion announcement.
    /// Placeholders: {mention}, {fromRank}, {toRank}.
    /// {fromRank} and {toRank} are the full English rank names (e.g. "Sergeant"),
    /// not the Discord role shorthand. Templates that don't include {fromRank}
    /// are intentional — sometimes "promoted to X" reads cleaner than
    /// "promoted from Y to X" when the new rank is the focus.
    ///
    /// ── Why so many variants ──
    /// Members fed back that automated promotion announcements felt
    /// less genuine when the same 1–2 templates rotated through every
    /// cycle — the repetition itself signals "this is from a script."
    /// 15 variants give a much wider distribution and break the
    /// "I've seen this exact line before" pattern. Combined with the
    /// 10-minute spacing between announcements, a typical evening's
    /// promotions now feel like individual recognition rather than a
    /// burst of identical notifications.
    /// </summary>
    private static readonly string[] AnnouncementTemplates =
    {
        "🎖️ Congratulations {mention}! You've been promoted from **{fromRank}** to **{toRank}**. Keep it up, soldier!",
        "⭐ {mention} has earned a promotion to **{toRank}**! Welcome to the next step up — hooah!",
        "🪖 A new stripe for {mention}! Promoted from **{fromRank}** to **{toRank}**. Outstanding work.",
        "🎉 Promotion time! {mention} moves up from **{fromRank}** to **{toRank}**. The 189th salutes you.",
        "⭐ {mention} earned a promotion to **{toRank}**. Well done.",
        "🎖️ Congratulations to {mention} on the promotion to **{toRank}** — you put in the work.",
        "🪖 {mention} has been promoted from **{fromRank}** to **{toRank}**. The 189th is proud to have you.",
        "📣 Attention all hands: {mention} is now **{toRank}**. Outstanding effort.",
        "🪖 New rank, same standard. Congrats to {mention} on reaching **{toRank}**.",
        "⭐ Promotion announcement: {mention} advances to **{toRank}**. Keep up the great work.",
        "🪖 {mention} just leveled up to **{toRank}**. Well earned.",
        "🎉 The 189th recognizes {mention} for promotion from **{fromRank}** to **{toRank}**. Outstanding.",
        "⭐ Up the chain goes {mention} — promoted to **{toRank}**. Excellent work.",
        "🪖 Stripes earned. {mention} is now **{toRank}**. The 189th thanks you for your service.",
        "🎉 {mention} answered the call and put in the time. Promoted to **{toRank}**. Well done.",
    };

    public PromotionService(
        IServiceProvider services,
        ILogger<PromotionService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Result of an attempted promotion.
    /// </summary>
    public record PromotionResult(
        bool Success,
        string NewRankName,
        string? Error = null,
        bool NicknameUpdated = false,
        string? NewNickname = null);

    /// <summary>
    /// Snapshot of a member's rank record for promotion math. AssignedAt falls
    /// back to the member's guild-join date when no RankHistory row exists;
    /// the seed fields default to (0, null) in that case, which is the normal
    /// "no seed applied" state.
    /// </summary>
    public record RankInfo(
        DateTime? AssignedAt,
        DateTime? SeedAppliedAt,
        int SeedEvents);

    /// <summary>
    /// Performs the role swap and nickname update for a promotion.
    /// Does NOT post any announcement — callers control whether and how to announce.
    /// Does NOT perform permission checks — callers are responsible for those.
    /// </summary>
    /// <param name="member">The member being promoted.</param>
    /// <param name="targetRankShorthand">The target rank shorthand (e.g. "pvt", "cpl").</param>
    public async Task<PromotionResult> PromoteAsync(
        SocketGuildUser member,
        string targetRankShorthand)
    {
        if (!RankMap.TryGetValue(targetRankShorthand, out var rankInfo))
        {
            return new PromotionResult(false, targetRankShorthand,
                Error: $"Invalid rank: {targetRankShorthand}");
        }

        var guild = member.Guild;

        var newRole = guild.Roles.FirstOrDefault(r => r.Name == rankInfo.NewRole);
        if (newRole is null)
        {
            return new PromotionResult(false, rankInfo.NewRole,
                Error: $"Role '{rankInfo.NewRole}' not found in guild");
        }

        // Already at target rank?
        if (member.Roles.Any(r => r.Name == rankInfo.NewRole))
        {
            return new PromotionResult(false, rankInfo.NewRole,
                Error: $"Member already has the {rankInfo.NewRole} role");
        }

        // ── Remove old role(s) ──────────────────────────────────────
        var removedRoles = new List<string>();

        if (string.Equals(targetRankShorthand, "sma", StringComparison.OrdinalIgnoreCase))
        {
            // SMA special case: remove either SGM or CSM (or both)
            foreach (var sourceName in SmaSourceRoles)
            {
                var sourceRole = member.Roles.FirstOrDefault(r => r.Name == sourceName);
                if (sourceRole is not null)
                {
                    await member.RemoveRoleAsync(sourceRole);
                    removedRoles.Add(sourceName);
                }
            }
        }
        else
        {
            var oldRole = member.Roles.FirstOrDefault(r => r.Name == rankInfo.OldRole);
            if (oldRole is not null)
            {
                await member.RemoveRoleAsync(oldRole);
                removedRoles.Add(rankInfo.OldRole);
            }
        }

        // ── Add new role ────────────────────────────────────────────
        await member.AddRoleAsync(newRole);

        // ── Update nickname ─────────────────────────────────────────
        var baseName = StripRankPrefix(member.DisplayName);
        var newNickname = $"{rankInfo.NewRole}.{baseName}";

        var nicknameUpdated = false;
        try
        {
            await member.ModifyAsync(p => p.Nickname = newNickname);
            nicknameUpdated = true;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Could not update nickname for {User}: {Error}",
                member.Username, ex.Message);
        }

        _logger.LogInformation(
            "Promotion applied: {Member} → {NewRank}. Removed: {Removed}. NicknameUpdated: {NickOk}",
            member.Username, rankInfo.NewRole,
            removedRoles.Count > 0 ? string.Join(", ", removedRoles) : "none",
            nicknameUpdated);

        return new PromotionResult(
            Success: true,
            NewRankName: rankInfo.NewRole,
            NicknameUpdated: nicknameUpdated,
            NewNickname: newNickname);
    }

    /// <summary>
    /// Strips any existing "RANK." or "RANK . " prefix from a display name,
    /// returning the base name. Used when building a new nickname after promotion.
    /// </summary>
    public string StripRankPrefix(string displayName)
    {
        var rankRoles = _config.GetRankRolesList();

        foreach (var rankRole in rankRoles)
        {
            // Match "RANK . Name" (spaced)
            var prefix = $"{rankRole} . ";
            if (displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return displayName[prefix.Length..];
            }

            // Match "RANK.Name" or "RANK. Name"
            var prefixNoDotSpace = $"{rankRole}.";
            if (displayName.StartsWith(prefixNoDotSpace, StringComparison.OrdinalIgnoreCase))
            {
                return displayName[prefixNoDotSpace.Length..].TrimStart();
            }
        }

        return displayName;
    }

    /// <summary>
    /// Posts a congratulatory announcement in the configured auto-promotion
    /// announcement channel. Safe to call even if the channel can't be found —
    /// will log a warning and move on.
    /// </summary>
    public async Task AnnouncePromotionAsync(
        SocketGuild guild,
        SocketGuildUser member,
        string fromRankShort,
        string toRankShort,
        string channelName)
    {
        var channel = guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            _logger.LogWarning(
                "Auto-promotion announcement channel '{Channel}' not found in guild {Guild}",
                channelName, guild.Name);
            return;
        }

        var fromFull = RankFullNames.TryGetValue(fromRankShort, out var f) ? f : fromRankShort;
        var toFull = RankFullNames.TryGetValue(toRankShort, out var t) ? t : toRankShort;

        // Pick a template at random. Random.Shared is thread-safe and avoids
        // the seed-bias problem of the previous (member.Id + ticks) % length
        // approach, which made each user's template selection partly biased
        // by their user ID. With 15 templates and a true random pick, two
        // back-to-back announcements have a 14/15 chance of using different
        // wording — high enough that the cycle as a whole reads as varied.
        var template = AnnouncementTemplates[Random.Shared.Next(AnnouncementTemplates.Length)];

        var message = template
            .Replace("{mention}", member.Mention)
            .Replace("{fromRank}", fromFull)
            .Replace("{toRank}", toFull);

        try
        {
            await channel.SendMessageAsync(
                message,
                allowedMentions: new AllowedMentions
                {
                    UserIds = new List<ulong> { member.Id },
                    MentionRepliedUser = false,
                });

            _logger.LogInformation(
                "Posted promotion announcement for {User} ({From} → {To}) in #{Channel}",
                member.Username, fromRankShort, toRankShort, channel.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post promotion announcement for {User} in #{Channel}",
                member.Username, channel.Name);
        }
    }

    /// <summary>
    /// Looks up when the member's current rank was assigned.
    /// Falls back to guild-join date if no RankHistory record exists.
    /// Returns null if neither is available.
    ///
    /// Thin wrapper around <see cref="GetRankInfoAsync"/> for callers that
    /// don't need the seed fields.
    /// </summary>
    public async Task<DateTime?> GetRankAssignedAtAsync(
        ulong guildId, SocketGuildUser member, CancellationToken ct = default)
    {
        var info = await GetRankInfoAsync(guildId, member, ct);
        return info.AssignedAt;
    }

    /// <summary>
    /// Looks up the full rank info (AssignedAt + seed fields) for a member.
    /// Falls back to guild-join date with an empty seed when no RankHistory
    /// row exists. Use this from callers that need to compute events-at-rank
    /// (AutoPromotionService, RosterExportService).
    /// </summary>
    public async Task<RankInfo> GetRankInfoAsync(
        ulong guildId, SocketGuildUser member, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var record = await db.RankHistories
            .FirstOrDefaultAsync(r => r.GuildId == guildId && r.UserId == member.Id, ct);

        if (record is not null)
        {
            return new RankInfo(
                AssignedAt: record.AssignedAt,
                SeedAppliedAt: record.SeedAppliedAt,
                SeedEvents: record.EventsAttendedAtRankBeforeBot);
        }

        // No record yet — fall back to guild-join date with no seed. Matters
        // for users who predate RankTrackingHandler but still have a rank role.
        return new RankInfo(
            AssignedAt: member.JoinedAt?.UtcDateTime,
            SeedAppliedAt: null,
            SeedEvents: 0);
    }
}