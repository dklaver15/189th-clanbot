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
    /// Templates for the per-user announcement (used by the /promote slash
    /// command — single, ceremonial post per manual promotion).
    /// Placeholders: {mention}, {fromRank}, {toRank} (full English rank names).
    /// AutoPromotionService no longer uses these — it builds a single
    /// combined post via GroupAnnouncementTemplates instead.
    /// </summary>
    private static readonly string[] SingleUserAnnouncementTemplates =
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

    /// <summary>
    /// Templates for the combined group-header announcement (used by
    /// AutoPromotionService — one post per cycle, with members listed
    /// underneath each header).
    /// Placeholders: {fromRank}, {toRank} (full English rank names).
    /// The {mention} placeholder is intentionally absent — callers append
    /// the mention list as separate lines beneath each rendered template.
    ///
    /// ── Why this exists ──
    /// Members fed back that automated promotion announcements felt less
    /// genuine when each promotion was its own post. A single combined
    /// post grouped by tier reads more like a clan recognition moment
    /// than a stream of bot notifications.
    ///
    /// ── Why "the following" phrasing works for n=1 ──
    /// "The following members are now Private" reads fine even with a
    /// single name beneath it. Splitting templates into singular/plural
    /// pairs would double the maintenance burden for marginal gain, so
    /// the templates are intentionally written to read naturally for
    /// any group size.
    /// </summary>
    private static readonly string[] GroupAnnouncementTemplates =
    {
        "🎖️ Congratulations to the following — promoted from **{fromRank}** to **{toRank}**. Keep it up, soldiers!",
        "⭐ The following members earned a promotion to **{toRank}** — hooah!",
        "🪖 New stripes for the following — promoted from **{fromRank}** to **{toRank}**. Outstanding work.",
        "🎉 Promotion time! The following members move up from **{fromRank}** to **{toRank}**. The 189th salutes you.",
        "⭐ The following members earned a promotion to **{toRank}**. Well done.",
        "🎖️ Congratulations to the following on the promotion to **{toRank}** — you put in the work.",
        "🪖 The following members have been promoted from **{fromRank}** to **{toRank}**. The 189th is proud to have you.",
        "📣 Attention all hands: the following members are now **{toRank}**. Outstanding effort.",
        "🪖 New rank, same standard. Congrats to the following on reaching **{toRank}**.",
        "⭐ Promotion announcement: the following members advance to **{toRank}**. Keep up the great work.",
        "🪖 The following members just leveled up to **{toRank}**. Well earned.",
        "🎉 The 189th recognizes the following members for promotion from **{fromRank}** to **{toRank}**. Outstanding.",
        "⭐ Up the chain goes the following — promoted to **{toRank}**. Excellent work.",
        "🪖 Stripes earned. The following members are now **{toRank}**. The 189th thanks you for your service.",
        "🎉 The following members answered the call and put in the time. Promoted to **{toRank}**. Well done.",
    };

    /// <summary>
    /// Discord's hard limit on a single message is 2000 characters. We split
    /// the combined announcement into multiple posts if a single rendered
    /// message would exceed this. In practice a typical cycle (3-10 promos)
    /// produces a message well under 1000 chars; the split path only fires
    /// for unusually large catch-up cycles.
    /// </summary>
    private const int DiscordMessageCharLimit = 2000;

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
    /// One member's promotion in a combined-announcement batch. Used as input
    /// to AnnounceCombinedPromotionsAsync — AutoPromotionService builds a list
    /// of these as it processes each member, then hands the whole list to the
    /// announcer at the end of the cycle.
    /// </summary>
    public record GroupedPromotion(
        SocketGuildUser Member,
        string FromRankShort,
        string ToRankShort);

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

        // ── Remove all other rank roles ─────────────────────────────
        // Defensive: strip every rank role except the target. This handles
        // step-by-step promotions (RCT → PVT), skip-ahead /promote calls
        // (RCT → SGT), the SMA special case (CSM or SGM → SMA), and any
        // pre-existing state where a member has multiple rank roles
        // (e.g. from an earlier failed promotion or manual role edit).
        //
        // Without this sweep, a single-role removal keyed off RankMap.OldRole
        // leaves stale lower-rank roles in place forever — once a member ends
        // up with two rank roles, GetCurrentRank picks the higher one and
        // every future promotion strips only the next one above the stale
        // role, perpetuating the broken state.
        var allRankNames = _config.GetRankRolesList()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rolesToRemove = member.Roles
            .Where(r => allRankNames.Contains(r.Name)
                     && !r.Name.Equals(rankInfo.NewRole, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var removedRoles = new List<string>();
        foreach (var role in rolesToRemove)
        {
            await member.RemoveRoleAsync(role);
            removedRoles.Add(role.Name);
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
    /// Posts a single-user congratulatory announcement. Used by the /promote
    /// slash command, where each manual promotion is its own post and the
    /// {mention} ping is intentional. AutoPromotionService uses
    /// AnnounceCombinedPromotionsAsync instead.
    /// </summary>
    public async Task AnnouncePromotionAsync(
        SocketGuild guild,
        SocketGuildUser member,
        string fromRankShort,
        string toRankShort,
        string channelName)
    {
        var channel = ResolveAnnouncementChannel(guild, channelName);
        if (channel is null) return;

        var fromFull = ResolveFullRankName(fromRankShort);
        var toFull   = ResolveFullRankName(toRankShort);

        var template = SingleUserAnnouncementTemplates[
            Random.Shared.Next(SingleUserAnnouncementTemplates.Length)];

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
    /// Posts a single combined announcement covering ALL promotions in a
    /// nightly cycle, grouped by tier (RCT→PVT first, then PVT→PFC, etc.).
    /// Used by AutoPromotionService at the end of each cycle.
    ///
    /// ── Format ──
    /// Per-tier section: a randomly-selected GroupAnnouncementTemplate
    /// rendered with {fromRank}/{toRank}, followed by each promoted member
    /// on its own line as a Discord mention. Sections separated by blank
    /// lines for readability.
    ///
    /// ── Group ordering ──
    /// Groups appear in the natural rank progression order (the order they
    /// appear in RankMap, which goes RCT→PVT, PVT→PFC, ..., SGM→CSM, CSM→SMA).
    /// This keeps the post scannable and matches how clan members think
    /// about promotion progress.
    ///
    /// ── Length handling ──
    /// Discord's hard 2000-char message limit applies. For typical cycles
    /// (3-10 promotions) the rendered message lands well under 1000 chars
    /// and posts in one shot. For oversized batches (e.g. a large catch-up
    /// after extended downtime), the message is split on group boundaries
    /// across multiple sequential posts, each independently picking a
    /// random template per group.
    ///
    /// All mentioned members are pinged. Discord caps mentions at 50 per
    /// message; in practice this isn't a constraint for promotion cycles.
    /// </summary>
    public async Task AnnounceCombinedPromotionsAsync(
        SocketGuild guild,
        IReadOnlyList<GroupedPromotion> promotions,
        string channelName)
    {
        if (promotions.Count == 0) return;

        var channel = ResolveAnnouncementChannel(guild, channelName);
        if (channel is null) return;

        // ── Group by tier, preserving rank progression order ──────────
        // Iterate RankMap in declaration order so RCT→PVT is rendered
        // first, then PVT→PFC, etc. Promotions with a target rank not in
        // RankMap (shouldn't happen) fall through to a final pseudo-group
        // ordered by their toRank string.
        var byTier = promotions
            .GroupBy(p => p.ToRankShort, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var orderedGroups = new List<(string fromShort, string toShort, List<GroupedPromotion> members)>();

        foreach (var (toShort, _) in RankMap)
        {
            if (!byTier.TryGetValue(toShort, out var members) || members.Count == 0) continue;
            // All members in a group share the same fromRank by construction
            // (since all promoted to the same toRank), so just take the first.
            var fromShort = members[0].FromRankShort;
            orderedGroups.Add((fromShort, toShort, members));
            byTier.Remove(toShort);
        }

        // Anything left over (unexpected target ranks) appended at the end
        foreach (var leftover in byTier)
        {
            orderedGroups.Add((leftover.Value[0].FromRankShort, leftover.Key, leftover.Value));
        }

        // ── Build per-group rendered text ─────────────────────────────
        var groupTexts = new List<string>(orderedGroups.Count);
        foreach (var (fromShort, toShort, members) in orderedGroups)
        {
            var template = GroupAnnouncementTemplates[
                Random.Shared.Next(GroupAnnouncementTemplates.Length)];

            var header = template
                .Replace("{fromRank}", ResolveFullRankName(fromShort))
                .Replace("{toRank}", ResolveFullRankName(toShort));

            var mentionLines = string.Join("\n", members.Select(m => m.Member.Mention));
            groupTexts.Add($"{header}\n{mentionLines}");
        }

        // ── Pack groups into messages within the char limit ───────────
        var messageBatches = PackGroupsIntoMessages(groupTexts);
        var allMentionedIds = promotions.Select(p => p.Member.Id).Distinct().ToList();

        for (int i = 0; i < messageBatches.Count; i++)
        {
            var body = messageBatches[i];

            try
            {
                await channel.SendMessageAsync(
                    body,
                    allowedMentions: new AllowedMentions
                    {
                        UserIds = allMentionedIds,
                        MentionRepliedUser = false,
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to post combined promotion announcement (batch {Batch}/{Total}) in #{Channel}",
                    i + 1, messageBatches.Count, channel.Name);
                // Continue to next batch — don't let one failure drop subsequent batches
            }
        }

        _logger.LogInformation(
            "Posted combined promotion announcement ({Members} members across {Groups} group(s), {Batches} message(s)) in #{Channel}",
            promotions.Count, orderedGroups.Count, messageBatches.Count, channel.Name);
    }

    /// <summary>
    /// Packs pre-rendered group texts into one or more messages each under
    /// the Discord char limit. Group boundaries are preserved — a single
    /// group is never split mid-list (unless it alone exceeds the limit,
    /// which would mean ~50+ mentions in one tier; not a real-world case).
    /// </summary>
    private static List<string> PackGroupsIntoMessages(List<string> groupTexts)
    {
        const string separator = "\n\n";
        var batches = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var group in groupTexts)
        {
            var addedLength = current.Length == 0 ? group.Length : separator.Length + group.Length;

            if (current.Length > 0 && current.Length + addedLength > DiscordMessageCharLimit)
            {
                batches.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0) current.Append(separator);
            current.Append(group);
        }

        if (current.Length > 0) batches.Add(current.ToString());
        return batches;
    }

    private SocketTextChannel? ResolveAnnouncementChannel(SocketGuild guild, string channelName)
    {
        var channel = guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(channelName, StringComparison.OrdinalIgnoreCase));

        if (channel is null)
        {
            _logger.LogWarning(
                "Auto-promotion announcement channel '{Channel}' not found in guild {Guild}",
                channelName, guild.Name);
        }

        return channel;
    }

    private static string ResolveFullRankName(string shortName) =>
        RankFullNames.TryGetValue(shortName, out var full) ? full : shortName;

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