using System.Collections.Concurrent;
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
///      (Reserve, Moderator, etc. as defined by
///      BotConfig.GetExemptRolesList()), strip the AWOL role if they have
///      it and resolve any pending AwolRecords. This is the real-time
///      counterpart to the self-healing pass in AwolCheckService Step 2 —
///      together they ensure that the moment a member becomes exempt, any
///      stuck AWOL state is cleaned up. Real-time path catches it within
///      seconds; the periodic sweep is the safety net for missed gateway
///      events (bot offline during the role change, etc.).
///
///   3. Rank-loss notices — when a member who held a rank role ends up
///      holding none, post to BotConfig.RankLossAlertChannelId. Nothing in
///      the bot removes a member's last rank role, so it always means
///      something outside the bot did, and it is easy to miss. See
///      TryAlertRankLossAsync for why it waits before deciding.
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
    private readonly RoleAuditLookupService _roleAudit;

    /// <summary>
    /// Value written to the "Logged By" column when a recruit is auto-logged.
    /// Left blank so officers can fill in the recruiter manually later without
    /// having to clear a placeholder first.
    /// </summary>
    private const string AutoLogPendingMarker = "";

    /// <summary>
    /// When the last rank-loss notice was posted for a member, keyed by guild
    /// and user, so BotConfig.RankLossAlertCooldownMinutes can stop a role that
    /// flaps from posting every time. Only holds members who actually lost a
    /// rank role, and expired entries are dropped after each post, so it stays
    /// small.
    /// </summary>
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), DateTime> _lastRankLossAlert = new();

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

    // Single FIFO queue for all role-change processing. See OnGuildMemberUpdated
    // for why the work leaves the gateway task, and why it is one global queue
    // rather than one per member.
    private readonly object _queueLock = new();
    private Task _roleChangeQueue = Task.CompletedTask;

    public RankTrackingHandler(
        IServiceProvider services,
        ILogger<RankTrackingHandler> logger,
        IOptions<BotConfig> config,
        GoogleSheetsService sheetsService,
        RoleAuditLookupService roleAudit)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
        _sheetsService = sheetsService;
        _roleAudit = roleAudit;
    }

    /// <summary>Register the GuildMemberUpdated event handler on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.GuildMemberUpdated += OnGuildMemberUpdated;
    }

    private Task OnGuildMemberUpdated(
        Cacheable<SocketGuildUser, ulong> beforeCacheable,
        SocketGuildUser after)
    {
        // We need the cached "before" state to compare roles
        if (!beforeCacheable.HasValue) return Task.CompletedTask;
        var before = beforeCacheable.Value;

        // Snapshot the post-change roles once, here, on the gateway task.
        // "after" is the live cache entry and Discord.NET mutates it in place, so
        // once the work below is queued rather than run inline, reading the member's
        // roles later would report whatever they are by then instead of what this
        // event actually carried. That matters: /promote issues RemoveRoleAsync then
        // AddRoleAsync, so one promotion arrives as two events, and each has to be
        // judged on its own state for the RankHistory bookkeeping to behave exactly
        // as it does today. The live "after" object is still used for the REST
        // calls, where current state is the right thing to act on.
        var afterRoles = after.Roles.ToList();

        // Cheap filter, deliberately left on the gateway task: pure set comparison,
        // no I/O, and it rejects the overwhelming majority of GuildMemberUpdated
        // events, so nickname, avatar and presence changes never reach the queue.
        var beforeRoleIds = before.Roles.Select(r => r.Id).ToHashSet();
        var afterRoleIds = afterRoles.Select(r => r.Id).ToHashSet();
        if (beforeRoleIds.SetEquals(afterRoleIds)) return Task.CompletedTask;

        // Everything past this point does DB work and REST calls: a role removal, a
        // nickname edit, an AWOL embed delete per open record, and a Google Sheets
        // write. Discord.NET dispatches gateway events one at a time and awaits each
        // handler, so running that inline delays every interaction queued behind it
        // past Discord's 3 second acknowledgement window, and those interactions die
        // with "The application did not respond".
        //
        // Queued rather than fired off with a bare Task.Run, and one global queue
        // rather than one per member, because that preserves today's ordering
        // exactly: one role change fully processed before the next begins. Two
        // concurrent passes for the same member could both see "no RankHistory row"
        // and insert duplicates, or apply a change and its correction backwards.
        lock (_queueLock)
        {
            _roleChangeQueue = _roleChangeQueue.ContinueWith(
                _ => RunQueuedRoleChangeAsync(before, after, afterRoles),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Backstop for the queue. The queue is one long task chain, so an exception
    /// escaping here would surface as an unobserved task fault instead of a log
    /// line. Each section inside has its own try/catch already; this covers the
    /// code between them.
    /// </summary>
    private async Task RunQueuedRoleChangeAsync(
        SocketGuildUser before,
        SocketGuildUser after,
        List<SocketRole> afterRoles)
    {
        try
        {
            await ProcessRoleChangeAsync(before, after, afterRoles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled error processing role change for {Username} in {Guild}",
                after.Username, after.Guild.Name);
        }
    }

    private async Task ProcessRoleChangeAsync(
        SocketGuildUser before,
        SocketGuildUser after,
        List<SocketRole> afterRoles)
    {
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
            await HandleExemptRoleGainedAsync(before, after, afterRoles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error handling exempt-role transition for {Username} in {Guild}",
                after.Username, after.Guild.Name);
        }

        // ── AWOL-removed handling ──
        // When the AWOL role is removed from a non-exempt member (officer clears
        // it by hand, mass cleanup, etc.), reset their activity window so the
        // periodic sweep doesn't immediately re-assign it. Own try/catch for the
        // same reasons as the exempt transition above.
        try
        {
            await HandleAwolRoleRemovedAsync(before, after, afterRoles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error handling AWOL-role removal for {Username} in {Guild}",
                after.Username, after.Guild.Name);
        }

        // Determine rank before and after using the configured rank roles list
        var rankRoles = _config.GetRankRolesList();
        var beforeRank = GetHighestRank(before.Roles.Select(r => r.Name), rankRoles);
        var afterRank = GetHighestRank(afterRoles.Select(r => r.Name), rankRoles);

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

                // Tell a human. Keyed off the role transition rather than the
                // RankHistory row on purpose: a member whose rank predates the
                // bot has no row, and their loss is exactly as worth knowing
                // about. Detached because it waits out a grace period before
                // deciding, and this runs on the shared role-change queue where
                // a delay would hold up every other member's role change.
                _ = TryAlertRankLossAsync(after, rankRecord?.RankName ?? beforeRank!, rankRoles);
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
                afterRoles.Any(r => r.Name.Equals("RCT", StringComparison.OrdinalIgnoreCase)) &&
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
    /// e.g. platoon changes for a member who is already exempt.
    ///
    /// Also a no-op if the member doesn't currently have AWOL — most exempt-role
    /// gains happen on members who weren't AWOL, and we don't want to spam the
    /// log or do unnecessary DB work for them.
    /// </summary>
    private async Task HandleExemptRoleGainedAsync(
        SocketGuildUser before, SocketGuildUser after, List<SocketRole> afterRoles)
    {
        var exemptRoles = _config.GetExemptRolesList();

        var wasExempt = before.Roles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        var isExempt = afterRoles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));

        // Only react to the "became exempt" transition.
        if (!isExempt || wasExempt) return;

        // Cheap check: skip if they don't have AWOL anyway.
        var awolRole = after.Guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        if (awolRole is null) return;

        var hasAwol = afterRoles.Any(r => r.Id == awolRole.Id);
        if (!hasAwol) return;

        // Identify which exempt role(s) triggered this for the audit log line.
        var triggeringRoles = string.Join(", ",
            afterRoles.Where(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
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
    /// If the member just LOST the AWOL role and is NOT exempt, reset their
    /// activity window so AwolCheckService gives them a full fresh window before
    /// it can flag them AWOL again, and clear any outstanding AWOL records /
    /// HQ embeds. No-op in all other cases.
    ///
    /// ── Why this exists ──
    /// AwolCheckService re-evaluates members on a timer from activity data plus
    /// current role state. Removing the AWOL role by hand changes neither, so the
    /// next sweep saw "inactive + no AWOL role" and immediately re-assigned the
    /// role and re-posted the member to #awol-list. Stamping WindowResetAt here
    /// makes the manual removal stick for a full window (Step 2's window-reset
    /// guard honors it the same way it honors JoinedAt). If the member is still
    /// inactive when that fresh window elapses, they get flagged again — by
    /// design.
    ///
    /// Exempt transitions are handled by HandleExemptRoleGainedAsync. We skip the
    /// reset when the member is currently exempt: their AWOL state is suppressed
    /// by the exemption itself, and resetting the window would be meaningless
    /// (exempt members are never evaluated for activity).
    /// </summary>
    private async Task HandleAwolRoleRemovedAsync(
        SocketGuildUser before, SocketGuildUser after, List<SocketRole> afterRoles)
    {
        var awolRole = after.Guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        if (awolRole is null) return;

        var hadAwol = before.Roles.Any(r => r.Id == awolRole.Id);
        var hasAwol = afterRoles.Any(r => r.Id == awolRole.Id);

        // Only react to the AWOL present → absent transition.
        if (!hadAwol || hasAwol) return;

        // Exempt members are handled by the exempt-transition path; their AWOL
        // suppression comes from the exemption, not a window reset.
        var exemptRoles = _config.GetExemptRolesList();
        var isExempt = afterRoles.Any(r =>
            exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        if (isExempt) return;

        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ── Reset the activity window (upsert the UserActivity row) ──
        var activity = await db.UserActivities
            .FirstOrDefaultAsync(a => a.GuildId == after.Guild.Id && a.UserId == after.Id);
        if (activity is null)
        {
            activity = new UserActivity
            {
                GuildId = after.Guild.Id,
                UserId = after.Id,
                Username = after.ToString() ?? after.Username,
            };
            db.UserActivities.Add(activity);
        }
        activity.WindowResetAt = now;

        // ── Resolve outstanding AWOL records and delete posted HQ embeds ──
        // Catch both pending (NotificationSent false, no message id) and posted
        // (message id stored) records, so removing the role also clears the
        // member from #awol-list. Mirrors AwolCheckService.CloseAwolRecordsAnd
        // DeleteEmbedsAsync; kept here because the sweep's window-reset guard
        // makes the member skip the branches that would otherwise call it.
        var openRecords = await db.AwolRecords
            .Where(r => r.GuildId == after.Guild.Id
                     && r.UserId == after.Id
                     && (!r.NotificationSent || r.NotificationMessageId != null))
            .ToListAsync();

        foreach (var record in openRecords)
        {
            if (record.NotificationMessageId.HasValue
                && record.NotificationChannelId.HasValue)
            {
                try
                {
                    var notifChannel = after.Guild.GetTextChannel(record.NotificationChannelId.Value);
                    if (notifChannel is not null)
                        await notifChannel.DeleteMessageAsync(record.NotificationMessageId.Value);
                }
                catch (Discord.Net.HttpException ex)
                    when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone (manually deleted, or cleared by /clear-awol-list).
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to delete AWOL embed for {Username} ({UserId}) on AWOL role removal (message {MessageId})",
                        after.Username, after.Id, record.NotificationMessageId.Value);
                }

                record.NotificationChannelId = null;
                record.NotificationMessageId = null;
            }

            record.NotificationSent = true;
            record.NotificationSentAt = now;
        }

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "AWOL role removed from {Username} ({UserId}) in {Guild} — reset activity window to {ResetAt:yyyy-MM-dd HH:mm} UTC " +
            "and resolved {Records} AWOL record(s). Member will not be re-flagged until a full activity window elapses.",
            after.Username, after.Id, after.Guild.Name, now, openRecords.Count);
    }

    /// <summary>
    /// Applies the "RCT." prefix to a member's nickname if they don't already have a rank prefix.
    /// Returns the new nickname on success, or null if no change was made or the update failed.
    /// </summary>
    public async Task<string?> TryApplyRctNicknameAsync(SocketGuildUser after, List<string> rankRoles)
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
    public async Task TryLogRecruitAsync(string recruitName, SocketGuildUser member)
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
    /// Posts a notice when a member who held a rank role ends up holding none.
    ///
    /// Nothing in ClanGuard takes a member's last rank role away: /promote and
    /// /demote always add a replacement, and every other role call in the bot
    /// touches only AWOL or Reserve. So this always reflects something done
    /// outside the bot, and it is easy to miss, because the nickname prefix and
    /// any platoon role stay exactly where they were while the member starts
    /// counting as unranked for the AWOL window.
    ///
    /// Waits <see cref="BotConfig.RankLossAlertGraceSeconds"/> and reads the
    /// member's roles again before posting. /promote and /demote strip the old
    /// rank role and then add the new one, so a normal promotion passes through
    /// a real "no rank role" state for a moment; re-checking is what tells the
    /// two apart. Runs detached from the role-change queue so the wait does not
    /// hold up other members' role changes, which means it owns its own
    /// try/catch: an exception here has nothing above it to log.
    /// </summary>
    private async Task TryAlertRankLossAsync(
        SocketGuildUser member, string lostRank, List<string> rankRoles)
    {
        try
        {
            if (!_config.RankLossAlertEnabled || _config.RankLossAlertChannelId == 0) return;

            var guild = member.Guild;
            var grace = Math.Max(0, _config.RankLossAlertGraceSeconds);
            if (grace > 0) await Task.Delay(TimeSpan.FromSeconds(grace));

            // Read the member back out of the guild cache rather than trusting
            // the object we were handed: we want current state, and Discord.NET
            // keeps the cached member up to date in place.
            var current = guild.GetUser(member.Id);
            if (current is null)
            {
                _logger.LogInformation(
                    "No rank-loss notice for {Username} ({UserId}): they are no longer in {Guild}",
                    member.Username, member.Id, guild.Name);
                return;
            }

            var rankNow = GetHighestRank(current.Roles.Select(r => r.Name), rankRoles);
            if (rankNow is not null)
            {
                _logger.LogDebug(
                    "Rank loss for {Username} ({UserId}) resolved within the grace period (now {Rank}), no notice posted",
                    current.Username, current.Id, rankNow);
                return;
            }

            if (guild.GetTextChannel(_config.RankLossAlertChannelId) is not SocketTextChannel channel)
            {
                _logger.LogWarning(
                    "RankLossAlertChannelId {ChannelId} did not resolve in {Guild}, so the rank-loss notice for {UserId} was not posted",
                    _config.RankLossAlertChannelId, guild.Name, member.Id);
                return;
            }

            // Claim the cooldown slot in one step. A role that flaps produces
            // one of these tasks per removal event, each with its own wait, so
            // two can reach this point together; checking and writing
            // separately would let both post.
            var cooldown = TimeSpan.FromMinutes(Math.Max(0, _config.RankLossAlertCooldownMinutes));
            var now = DateTime.UtcNow;
            var claimed = _lastRankLossAlert.AddOrUpdate(
                (guild.Id, member.Id),
                now,
                (_, previous) => now - previous < cooldown ? previous : now);

            if (claimed != now)
            {
                _logger.LogDebug(
                    "Skipping rank-loss notice for {Username} ({UserId}): one was posted {Minutes:F0} minutes ago",
                    current.Username, current.Id, (now - claimed).TotalMinutes);
                return;
            }

            // Name the culprit. ClanGuard records no actor of its own on a rank
            // change, so this reads it back out of Discord's audit log. Scoped to
            // the last hour because the removal happened moments ago: a tight
            // cutoff usually settles it inside one page.
            var actor = await _roleAudit.FindRoleRemovalAsync(
                guild, member.Id, lostRank, DateTime.UtcNow.AddHours(-1));

            var byLine = actor is null
                ? "The server audit log has no matching entry, so this may predate its 45 day retention."
                : $"Removed by {(actor.ActorId is ulong id ? MentionUtils.MentionUser(id) : actor.ActorName)}"
                  + (actor.ActorIsClanGuard ? " (that is ClanGuard itself, which should not happen)" : string.Empty)
                  + ".";

            var eb = new EmbedBuilder()
                .WithColor(Color.Orange)
                .WithTitle("Rank role removed")
                .WithDescription(
                    $"{current.Mention} no longer holds any rank role. ClanGuard never takes the last "
                    + $"rank role away, so this came from somewhere else. {byLine}")
                .AddField("Member", current.DisplayName, true)
                .AddField("Was", lostRank, true)
                .AddField("User ID", current.Id.ToString(), true)
                .WithFooter("They now count as unranked, which shortens their AWOL window. /timeline has the full history.")
                .WithCurrentTimestamp();

            if (actor?.Reason is string reason)
                eb.AddField("Reason given", Truncate(reason, 512));

            try
            {
                await channel.SendMessageAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);
            }
            catch
            {
                // Give the slot back, or a failed post would silence the retry
                // that a later removal event would otherwise produce.
                _lastRankLossAlert.TryRemove((guild.Id, member.Id), out _);
                throw;
            }

            PruneRankLossAlertCooldowns(cooldown);

            _logger.LogInformation(
                "Posted rank-loss notice for {Username} ({UserId}) in {Guild}: lost {Rank}",
                current.Username, current.Id, guild.Name, lostRank);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post the rank-loss notice for {Username} ({UserId})",
                member.Username, member.Id);
        }
    }

    /// <summary>
    /// Clips text to fit an embed field, marking the cut so a reader can tell a
    /// short reason from a trimmed one.
    /// </summary>
    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// Drops cooldown entries that have aged out. Called after each post so the
    /// dictionary only ever holds members inside the current cooldown window
    /// instead of growing for the life of the process.
    /// </summary>
    private void PruneRankLossAlertCooldowns(TimeSpan cooldown)
    {
        var cutoff = DateTime.UtcNow - cooldown;
        foreach (var entry in _lastRankLossAlert)
        {
            if (entry.Value < cutoff) _lastRankLossAlert.TryRemove(entry.Key, out _);
        }
    }

    /// <summary>
    /// Returns the highest rank role name from the member's roles, or null if they have no rank roles.
    /// </summary>
    public static string? GetHighestRank(IEnumerable<string> memberRoleNames, List<string> rankRoles)
    {
        for (int i = rankRoles.Count - 1; i >= 0; i--)
        {
            if (memberRoleNames.Any(r => r.Equals(rankRoles[i], StringComparison.OrdinalIgnoreCase)))
                return rankRoles[i];
        }
        return null;
    }
}