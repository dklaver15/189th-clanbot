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
/// Handles the GuildMemberUpdated event for two role-driven business actions:
///
///   1. Rank tracking — when a member's rank role changes, update RankHistory
///      with the exact timestamp. Auto-prefix nicknames on RCT gain and
///      auto-log new recruits to the Google Sheet.
///
///   2. Exempt-role transitions — when a member GAINS an exempt role
///      (Reserve, Admin, Moderator, Retired, etc. as defined by
///      BotConfig.GetExemptRolesList()), strip the AWOL role if they have
///      it and resolve any pending AwolRecords. This is the real-time
///      counterpart to the self-healing pass in AwolCheckService Step 2 —
///      together they ensure that the moment a member becomes exempt, any
///      stuck AWOL state is cleaned up. Real-time path catches it within
///      seconds; the periodic sweep is the safety net for missed gateway
///      events (bot offline during the role change, etc.).
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

    /// <summary>
    /// Maximum apparent demotion (in number of rank-list indices) the realtime
    /// handler will accept as a legitimate /demote operation. Larger drops are
    /// almost certainly the result of a role-name mismatch between Discord and
    /// the configured RankRoles list — a senior member's actual rank role
    /// isn't being recognized, so GetHighestRank "sees" only a stale junior
    /// rank role they happen to also hold and reports them as collapsing to
    /// that rank. Real /demote operations are typically 1-2 ranks at a time;
    /// this threshold gives that headroom while catching the suspicious
    /// large-drop pattern. Drops larger than this are refused with a warning;
    /// genuine large demotions can still be applied via direct DB update or
    /// staged /demote calls.
    /// </summary>
    private const int MaxRealtimeDemotionGap = 2;

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

        // ── Exempt-role transition handling ──
        // Run this BEFORE the rank-change logic because:
        //   1. Reserve / Admin / etc. can be added without any rank change,
        //      and the rank-change branch returns early when ranks match.
        //   2. The two operations touch different DB tables (AwolRecords vs
        //      RankHistories) so there's no ordering dependency.
        // Wrapped in its own try/catch so a failure here does not block
        // the rank-tracking logic below.
        try
        {
            await HandleExemptRoleGainedAsync(before, after);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error handling exempt-role transition for {Username} in {Guild}",
                after.Username, after.Guild.Name);
        }

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
                    // Append the "Removed" entry to the rank-change log BEFORE
                    // we remove the RankHistory row, so it can read the prior
                    // rank name from the in-memory entity. Both writes flush
                    // together via SaveChangesAsync below.
                    RankChangeLogHelper.StageChange(
                        db,
                        guildId,
                        userId,
                        fromRank: rankRecord.RankName,
                        toRank:   null,
                        changedAt: DateTime.UtcNow);

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
                var now = DateTime.UtcNow;
                db.RankHistories.Add(new RankHistory
                {
                    GuildId = guildId,
                    UserId = userId,
                    RankName = afterRank,
                    AssignedAt = now,
                    EventsAttendedAtRankBeforeBot = 0,
                    SeedAppliedAt = null,
                });
                // "Initial" entry — FromRank=null marks "no prior rank in our
                // records." Could be a brand-new member or a rejoin whose
                // stale RankHistory row was pruned by MemberLifecycleHandler.
                RankChangeLogHelper.StageChange(
                    db,
                    guildId,
                    userId,
                    fromRank: null,
                    toRank:   afterRank,
                    changedAt: now);

                _logger.LogInformation(
                    "Rank assigned: {Username} → {NewRank} in {Guild}",
                    after.Username, afterRank, after.Guild.Name);
            }
            else
            {
                // ── Defensive guard against silent overwrites ──
                // Compute the rank-list index gap between the existing recorded
                // rank and the rank we're about to assign. A drop of more than
                // MaxRealtimeDemotionGap ranks is almost certainly the result of
                // a role-name mismatch between Discord and the configured
                // RankRoles list (e.g. a senior member's actual rank role isn't
                // in the config, so GetHighestRank "sees" only their stale RCT
                // role and reports them as dropping from senior rank to RCT).
                // Refuse the update and log loudly. Real /demote operations of
                // 1-2 ranks at a time still flow through normally; larger
                // intentional demotions must be applied out-of-band.
                var oldIdx = rankRoles.FindIndex(r =>
                    r.Equals(rankRecord.RankName, StringComparison.OrdinalIgnoreCase));
                var newIdx = rankRoles.FindIndex(r =>
                    r.Equals(afterRank, StringComparison.OrdinalIgnoreCase));

                if (oldIdx >= 0 && newIdx >= 0 && (oldIdx - newIdx) > MaxRealtimeDemotionGap)
                {
                    _logger.LogWarning(
                        "Refusing realtime rank update: {Username} appears to drop " +
                        "from {OldRank} to {NewRank} (gap = {Gap} ranks > {Max}). " +
                        "Almost certainly a role-name mismatch with RankRoles config, " +
                        "not a real demotion. Preserving existing RankHistory and " +
                        "skipping side effects. Verify role configuration before " +
                        "making manual corrections.",
                        after.Username, rankRecord.RankName, afterRank,
                        oldIdx - newIdx, MaxRealtimeDemotionGap);
                    return; // skip RankHistory mutation AND side effects
                }

                // Rank changed — update with exact timestamp and clear the
                // seed fields. The previous rank's seed represented events
                // already credited AT THAT rank; it must not carry into the
                // new rank. If the person needs credit at their new rank
                // from the spreadsheet, that's a fresh /seed-promotion-credit
                // run (which would clear here again and re-apply).
                _logger.LogInformation(
                    "Rank change: {Username} {OldRank} → {NewRank} in {Guild} (clearing seed)",
                    after.Username, rankRecord.RankName, afterRank, after.Guild.Name);

                // Stage the rank-change log entry BEFORE we mutate rankRecord
                // so the helper sees the prior name. Both writes flush together
                // via the single SaveChangesAsync below.
                var changedAt = DateTime.UtcNow;
                RankChangeLogHelper.StageChange(
                    db,
                    guildId,
                    userId,
                    fromRank: rankRecord.RankName,
                    toRank:   afterRank,
                    changedAt: changedAt);

                rankRecord.RankName = afterRank;
                rankRecord.AssignedAt = changedAt;
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
    /// If the member just GAINED an exempt role (Reserve, Admin, Moderator, etc.)
    /// AND currently holds the AWOL role, strip AWOL and resolve any pending
    /// AwolRecords. No-op in all other cases.
    ///
    /// "Gained an exempt role" = was not exempt before, is exempt now. We do NOT
    /// fire on every role change for a member who happens to be exempt — only
    /// on the actual exempt transition. This avoids redundant DB lookups on
    /// e.g. platoon changes for a Retired member.
    ///
    /// Also a no-op if the member doesn't currently have AWOL — most exempt-role
    /// gains happen on members who weren't AWOL, and we don't want to spam the
    /// log or do unnecessary DB work for them.
    /// </summary>
    private async Task HandleExemptRoleGainedAsync(SocketGuildUser before, SocketGuildUser after)
    {
        var exemptRoles = _config.GetExemptRolesList();

        var wasExempt = before.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        var isExempt = after.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));

        // Only react to the "became exempt" transition.
        if (!isExempt || wasExempt) return;

        // Cheap check: skip if they don't have AWOL anyway.
        var awolRole = after.Guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        if (awolRole is null) return;

        var hasAwol = after.Roles.Any(r => r.Id == awolRole.Id);
        if (!hasAwol) return;

        // Identify which exempt role(s) triggered this for the audit log line.
        var triggeringRoles = string.Join(", ",
            after.Roles.Where(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
                       .Select(r => r.Name));

        try
        {
            await after.RemoveRoleAsync(awolRole,
                new RequestOptions
                {
                    AuditLogReason = $"AWOL auto-cleared: member gained exempt role(s) [{triggeringRoles}]"
                });

            _logger.LogInformation(
                "Real-time AWOL clear: removed AWOL from {Username} ({UserId}) in {Guild} — gained exempt role(s): {Roles}",
                after.Username, after.Id, after.Guild.Name, triggeringRoles);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(
                "Could not remove AWOL from {Username}: insufficient permissions. " +
                "AwolCheckService self-heal will retry on next cycle.",
                after.Username);
            // Fall through and resolve records anyway — the role stuck around but
            // the member is exempt, so suppress the pending notification.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to remove AWOL from {Username} on exempt-role transition",
                after.Username);
            return; // don't resolve records if we can't even remove the role
        }

        // Resolve any pending AwolRecords so they don't surface notifications
        // for someone who is no longer functionally AWOL.
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var pendingRecords = await db.AwolRecords
                .Where(r => r.GuildId == after.Guild.Id
                         && r.UserId == after.Id
                         && !r.NotificationSent)
                .ToListAsync();

            if (pendingRecords.Count > 0)
            {
                foreach (var record in pendingRecords)
                {
                    record.NotificationSent = true;
                    record.NotificationSentAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "Resolved {Count} pending AwolRecord(s) for {Username} after exempt-role transition",
                    pendingRecords.Count, after.Username);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to resolve AwolRecords for {Username} after exempt-role transition",
                after.Username);
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