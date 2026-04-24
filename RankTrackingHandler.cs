using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the GuildMemberUpdated event to detect rank role changes in real-time.
/// When a member's rank changes, updates the RankHistory record with the exact timestamp.
/// Also auto-prefixes nicknames on RCT gain and auto-logs new recruits to the Google Sheet.
///
/// ── Seed field handling ──
/// Whenever this handler updates RankHistory.RankName, it also resets the seed
/// fields (EventsAttendedAtRankBeforeBot + SeedAppliedAt) to their empty state.
/// The seed is rank-specific — it represents events already counted at the
/// person's previous rank when they were backfilled from the manual spreadsheet
/// via /seed-promotion-credit. When rank changes (promotion or demotion), that
/// credit has done its job and must NOT carry over to the new rank, or the
/// person would start their new rank with a head start of inherited events.
/// This rule is mirrored in RosterExportService's nightly rank-change detection
/// path; the two should stay in sync.
/// </summary>
public class RankTrackingHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RankTrackingHandler> _logger;
    private readonly BotConfig _config;
    private readonly GoogleSheetsService _sheetsService;

    /// <summary>
    /// Value written to the "Logged By" column when a recruit is auto-logged.
    /// Left blank so officers can fill in the recruiter manually later without
    /// having to clear a placeholder first.
    /// </summary>
    private const string AutoLogPendingMarker = "";

    public RankTrackingHandler(
        IServiceProvider services,
        ILogger<RankTrackingHandler> logger,
        IOptions<BotConfig> config,
        GoogleSheetsService sheetsService)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
        _sheetsService = sheetsService;
    }

    /// <summary>Register the GuildMemberUpdated event handler on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.GuildMemberUpdated += OnGuildMemberUpdated;
    }

    private async Task OnGuildMemberUpdated(
        Cacheable<SocketGuildUser, ulong> beforeCacheable,
        SocketGuildUser after)
    {
        // We need the cached "before" state to compare roles
        if (!beforeCacheable.HasValue) return;
        var before = beforeCacheable.Value;

        // Quick check: did roles change at all?
        var beforeRoleIds = before.Roles.Select(r => r.Id).ToHashSet();
        var afterRoleIds = after.Roles.Select(r => r.Id).ToHashSet();
        if (beforeRoleIds.SetEquals(afterRoleIds)) return;

        // Determine rank before and after using the configured rank roles list
        var rankRoles = _config.GetRankRolesList();
        var beforeRank = GetHighestRank(before.Roles.Select(r => r.Name), rankRoles);
        var afterRank = GetHighestRank(after.Roles.Select(r => r.Name), rankRoles);

        // If the rank didn't change, ignore (could be a non-rank role change)
        if (string.Equals(beforeRank, afterRank, StringComparison.OrdinalIgnoreCase)) return;

        var guildId = after.Guild.Id;
        var userId = after.Id;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var rankRecord = await db.RankHistories
                .FirstOrDefaultAsync(r => r.GuildId == guildId && r.UserId == userId);

            if (afterRank is null)
            {
                // User lost all rank roles — remove the record
                if (rankRecord is not null)
                {
                    db.RankHistories.Remove(rankRecord);
                    _logger.LogInformation(
                        "Rank removed: {Username} lost rank {OldRank} in {Guild}",
                        after.Username, beforeRank, after.Guild.Name);
                }
            }
            else if (rankRecord is null)
            {
                // First time seeing a rank for this user. Explicitly zero out
                // the seed fields so it's obvious at a glance that a fresh rank
                // record starts without any one-time spreadsheet credit applied.
                db.RankHistories.Add(new RankHistory
                {
                    GuildId = guildId,
                    UserId = userId,
                    RankName = afterRank,
                    AssignedAt = DateTime.UtcNow,
                    EventsAttendedAtRankBeforeBot = 0,
                    SeedAppliedAt = null,
                });
                _logger.LogInformation(
                    "Rank assigned: {Username} → {NewRank} in {Guild}",
                    after.Username, afterRank, after.Guild.Name);
            }
            else
            {
                // Rank changed — update with exact timestamp and clear the
                // seed fields. The previous rank's seed represented events
                // already credited AT THAT rank; it must not carry into the
                // new rank. If the person needs credit at their new rank
                // from the spreadsheet, that's a fresh /seed-promotion-credit
                // run (which would clear here again and re-apply).
                _logger.LogInformation(
                    "Rank change: {Username} {OldRank} → {NewRank} in {Guild} (clearing seed)",
                    after.Username, rankRecord.RankName, afterRank, after.Guild.Name);

                rankRecord.RankName = afterRank;
                rankRecord.AssignedAt = DateTime.UtcNow;
                rankRecord.EventsAttendedAtRankBeforeBot = 0;
                rankRecord.SeedAppliedAt = null;
            }

            await db.SaveChangesAsync();

            // ── Detect "RCT gained" transition (used by two side effects below) ──
            // Fires when the RCT role is present after but was not present before.
            var rctGained =
                after.Roles.Any(r => r.Name.Equals("RCT", StringComparison.OrdinalIgnoreCase)) &&
                !before.Roles.Any(r => r.Name.Equals("RCT", StringComparison.OrdinalIgnoreCase));

            if (rctGained)
            {
                // Side effect 1: auto-prefix the nickname to "RCT.{DisplayName}".
                var newNickname = await TryApplyRctNicknameAsync(after, rankRoles);

                // Side effect 2: auto-log the recruit to the Google Sheet.
                // Uses the post-prefix nickname when available so the logged name
                // matches what appears in Discord. Falls back to display name if
                // the nickname update was skipped or failed.
                var recruitName = !string.IsNullOrWhiteSpace(newNickname)
                    ? newNickname!
                    : after.DisplayName;
                _ = TryLogRecruitAsync(recruitName, after);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking rank change for {Username} in {Guild}",
                after.Username, after.Guild.Name);
        }
    }

    /// <summary>
    /// Applies the "RCT." prefix to a member's nickname if they don't already have a rank prefix.
    /// Returns the new nickname on success, or null if no change was made or the update failed.
    /// </summary>
    private async Task<string?> TryApplyRctNicknameAsync(SocketGuildUser after, List<string> rankRoles)
    {
        try
        {
            var displayName = after.DisplayName;

            // Don't prefix if they already have a rank prefix
            var hasPrefix = rankRoles.Any(r =>
                displayName.StartsWith($"{r}.", StringComparison.OrdinalIgnoreCase) ||
                displayName.StartsWith($"{r} . ", StringComparison.OrdinalIgnoreCase));

            if (hasPrefix) return null;

            var newNickname = $"RCT.{displayName}";
            await after.ModifyAsync(p => p.Nickname = newNickname);
            _logger.LogInformation(
                "Auto-nickname: {Username} → {NewNick} in {Guild}",
                after.Username, newNickname, after.Guild.Name);

            return newNickname;
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(
                "Could not set RCT nickname for {Username}: insufficient permissions",
                after.Username);
            return null;
        }
    }

    /// <summary>
    /// Writes a row to the Recruit Log sheet with "Pending" as the recruiter.
    /// Runs fire-and-forget so a sheets outage cannot block the rest of the rank-tracking flow.
    /// Errors are caught and logged — the member's role/nickname state is already correct.
    /// </summary>
    private async Task TryLogRecruitAsync(string recruitName, SocketGuildUser member)
    {
        try
        {
            await _sheetsService.WriteRecruitLogAsync(recruitName, AutoLogPendingMarker, DateTime.UtcNow);
            _logger.LogInformation(
                "Auto-logged recruit {RecruitName} ({UserId}) to sheet with blank recruiter",
                recruitName, member.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to auto-log recruit {RecruitName} ({UserId}) to sheet",
                recruitName, member.Id);
        }
    }

    /// <summary>
    /// Returns the highest rank role name from the member's roles, or null if they have no rank roles.
    /// </summary>
    private static string? GetHighestRank(IEnumerable<string> memberRoleNames, List<string> rankRoles)
    {
        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Any(r => r.Equals(rankRoles[i], StringComparison.OrdinalIgnoreCase)))
                return rankRoles[i];
        }
        return null;
    }
}