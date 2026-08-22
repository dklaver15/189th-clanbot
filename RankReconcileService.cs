using ClanGuardBot.Data;
using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Self-healing for rank roles the bot never saw arrive.
///
/// ── The gap this closes ──
/// RankTrackingHandler is the only thing in the codebase that ever creates a
/// RankHistory row, and it works purely by diffing before/after roles on a live
/// GuildMemberUpdated event. Four things can stop that event from landing:
///
///   1. The role was already on the member when they joined. Discord's
///      onboarding role picker applies selections as part of the join, so
///      there is no later role update to diff and the handler is never called.
///   2. The member was not in Discord.NET's cache, so there is no before-state.
///   3. The bot was down or restarting when the role was granted.
///   4. The role-change queue was stalled behind a hung item.
///
/// In every one of those cases the member ends up holding a rank role with no
/// RankHistory row, and because nothing else in the bot ever writes that table,
/// nothing repairs it. They show a blank "Current Rank" on /timeline forever,
/// their promotion clock never starts, and a new RCT never gets the nickname
/// prefix or the recruit-log row. This service is that missing repair pass.
///
/// ── Two paths ──
/// The join path handles the case that actually bites: someone joins, ends up
/// ranked, and the live handler saw nothing. It waits
/// RankReconcileJoinDelaySeconds first so the live handler gets its chance and
/// any onboarding roles settle, then repairs that one member. It runs live.
///
/// The sweep path is the safety net for everyone else, on startup and every
/// RankReconcileSweepHours. It defaults to DRY RUN, and deliberately so: on a
/// server this old the sweep will find long-standing members whose rank predates
/// the bot, and writing rows for all of them at once would start their promotion
/// clocks together. Read what the first run reports, then set
/// RankReconcileSweepDryRun to false if the list looks right.
///
/// ── Dating the repaired row ──
/// AssignedAt drives every promotion calculation, so inventing it is not
/// harmless. Preference order: the audit-log entry that actually added the role,
/// then the member's join timestamp, then now. Anything resolving older than
/// RankReconcileMaxBackdateDays falls back to now, which stops a member with
/// years of tenure from being handed years of back-credit toward their next
/// promotion by a repair pass.
///
/// ── Never touches an existing row ──
/// A member who already has a RankHistory row is skipped outright, even when the
/// recorded rank disagrees with their roles. Correcting a live rank is
/// RankTrackingHandler's job and it has guards this service does not (see
/// MaxRealtimeDemotionGap). This service only ever fills in what is missing.
/// </summary>
public class RankReconcileService : BackgroundService
{
    /// <summary>
    /// Wait before the first sweep. Sits behind MemberRosterReconciler's own
    /// 60 second grace so the two startup passes do not fight over the member
    /// download or the audit-log rate limit.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(120);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<RankReconcileService> _logger;
    private readonly BotConfig _config;
    private readonly RoleAuditLookupService _roleAudit;
    private readonly RankTrackingHandler _rankHandler;

    public RankReconcileService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<RankReconcileService> logger,
        IOptions<BotConfig> config,
        RoleAuditLookupService roleAudit,
        RankTrackingHandler rankHandler)
    {
        _services    = services;
        _client      = client;
        _logger      = logger;
        _config      = config.Value;
        _roleAudit   = roleAudit;
        _rankHandler = rankHandler;
    }

    public void Register(DiscordSocketClient client)
    {
        client.UserJoined += OnUserJoined;
    }

    // ── Join path ────────────────────────────────────────────────────────

    private Task OnUserJoined(SocketGuildUser member)
    {
        if (!_config.RankReconcileEnabled || member.IsBot) return Task.CompletedTask;

        // Detached: this waits out a delay, and UserJoined runs on the gateway
        // task where anything slow delays every event queued behind it.
        _ = ReconcileAfterJoinAsync(member.Guild.Id, member.Id);
        return Task.CompletedTask;
    }

    private async Task ReconcileAfterJoinAsync(ulong guildId, ulong userId)
    {
        try
        {
            var delay = Math.Max(0, _config.RankReconcileJoinDelaySeconds);
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay));

            var guild = _client.GetGuild(guildId);
            var member = guild?.GetUser(userId);
            if (guild is null || member is null) return;

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var rankRoles = _config.GetRankRolesList();

            var outcome = await ReconcileMemberAsync(
                db, guild, member, rankRoles,
                dryRun: false,
                allowSideEffects: true);

            if (outcome.Repaired)
            {
                _logger.LogInformation(
                    "Rank reconcile (join): repaired {Username} ({UserId}) as {Rank}, dated {AssignedAt:u} from {Source}. " +
                    "The live role-change event never arrived for them",
                    member.Username, member.Id, outcome.Rank, outcome.AssignedAt, outcome.DateSource);

                await PostRepairNoticeAsync(guild, member, outcome, "they joined");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Rank reconcile (join) failed for {UserId} in guild {GuildId}", userId, guildId);
        }
    }

    // ── Sweep path ───────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.RankReconcileEnabled)
        {
            _logger.LogInformation("Rank reconcile is switched off (RankReconcileEnabled = false)");
            return;
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        var hours = Math.Max(1, _config.RankReconcileSweepHours);
        _logger.LogInformation(
            "RankReconcileService started; sweeping on startup and every {Hours}h (dry run: {DryRun})",
            hours, _config.RankReconcileSweepDryRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rank reconcile sweep failed; will retry next interval");
            }

            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var guild = _client.Guilds.FirstOrDefault();
        if (guild is null)
        {
            _logger.LogWarning("Rank reconcile: no guild connected, skipping sweep");
            return;
        }

        // The sweep decides "has no RankHistory row" from the member cache, so
        // an incomplete cache would look like a pile of missing rows.
        await guild.DownloadUsersAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var rankRoles = _config.GetRankRolesList();
        var dryRun = _config.RankReconcileSweepDryRun;

        // One query for the whole guild rather than one per member.
        var tracked = (await db.RankHistories
            .Where(r => r.GuildId == guild.Id)
            .Select(r => r.UserId)
            .ToListAsync(ct))
            .ToHashSet();

        var candidates = guild.Users
            .Where(u => !u.IsBot)
            .Where(u => !tracked.Contains(u.Id))
            .Where(u => RankTrackingHandler.GetHighestRank(u.Roles.Select(r => r.Name), rankRoles) is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogDebug("Rank reconcile sweep: nothing to repair in {Guild}", guild.Name);
            return;
        }

        _logger.LogInformation(
            "Rank reconcile sweep: {Count} member(s) in {Guild} hold a rank role with no RankHistory row{Mode}",
            candidates.Count, guild.Name, dryRun ? " (DRY RUN, nothing will be written)" : string.Empty);

        var repaired = 0;
        foreach (var member in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ReconcileMemberAsync(
                    db, guild, member, rankRoles,
                    dryRun: dryRun,
                    allowSideEffects: !dryRun);

                if (outcome.Repaired) repaired++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Rank reconcile sweep: failed on {Username} ({UserId})", member.Username, member.Id);
            }
        }

        _logger.LogInformation(
            "Rank reconcile sweep finished: {Repaired} of {Count} {Verb}",
            repaired, candidates.Count, dryRun ? "would be repaired" : "repaired");
    }

    // ── Shared repair ────────────────────────────────────────────────────

    /// <summary>
    /// Result of looking at one member. <see cref="Repaired"/> is false for a
    /// member who needed nothing, and also for a dry run that only reported.
    /// </summary>
    private sealed record ReconcileOutcome(
        bool Repaired,
        string? Rank,
        DateTime AssignedAt,
        string DateSource);

    private static readonly ReconcileOutcome NoAction =
        new(false, null, default, string.Empty);

    /// <summary>
    /// Creates the missing RankHistory row for one member, or does nothing.
    /// Saves its own changes so a failure on one member in a sweep cannot
    /// discard the repairs already made for the others.
    /// </summary>
    private async Task<ReconcileOutcome> ReconcileMemberAsync(
        BotDbContext db,
        SocketGuild guild,
        SocketGuildUser member,
        List<string> rankRoles,
        bool dryRun,
        bool allowSideEffects)
    {
        var rank = RankTrackingHandler.GetHighestRank(member.Roles.Select(r => r.Name), rankRoles);
        if (rank is null) return NoAction;

        // Never touch a member the bot is already tracking. A wrong recorded
        // rank is the live handler's problem to fix, with its own guards.
        var existing = await db.RankHistories
            .FirstOrDefaultAsync(r => r.GuildId == guild.Id && r.UserId == member.Id);
        if (existing is not null) return NoAction;

        var (assignedAt, dateSource) = await ResolveAssignedAtAsync(guild, member, rank);

        if (dryRun)
        {
            _logger.LogInformation(
                "Rank reconcile (dry run): would record {Username} ({UserId}) as {Rank}, dated {AssignedAt:u} from {Source}",
                member.Username, member.Id, rank, assignedAt, dateSource);
            return NoAction;
        }

        db.RankHistories.Add(new RankHistory
        {
            GuildId = guild.Id,
            UserId  = member.Id,
            RankName = rank,
            AssignedAt = assignedAt,
            // A repaired row starts with no spreadsheet credit applied, exactly
            // like a fresh one written by the live handler.
            EventsAttendedAtRankBeforeBot = 0,
            SeedAppliedAt = null,
        });

        // FromRank null marks "no prior rank in our records", matching what the
        // live handler writes for a first observed rank. Dated to match the row
        // so the timeline entry lands where the change actually happened rather
        // than where we noticed it.
        RankChangeLogHelper.StageChange(
            db, guild.Id, member.Id,
            fromRank: null, toRank: rank, changedAt: assignedAt);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            // The unique index on (GuildId, UserId) caught a row the live
            // handler wrote in between our read and this save. That is the
            // correct outcome, not an error: the member is tracked either way.
            db.ChangeTracker.Clear();
            _logger.LogDebug(ex,
                "Rank reconcile: {Username} ({UserId}) was recorded by the live handler first, nothing to repair",
                member.Username, member.Id);
            return NoAction;
        }

        if (allowSideEffects) await RunRecruitSideEffectsAsync(member, rank, rankRoles);

        return new ReconcileOutcome(true, rank, assignedAt, dateSource);
    }

    /// <summary>
    /// The two things a new RCT gets from the live path that a missing event
    /// also loses: the nickname prefix and the recruit-log row.
    ///
    /// Gated on the member having joined within
    /// RankReconcileSideEffectWindowHours. Without that window a sweep over a
    /// long-standing server would rename members who have gone years without a
    /// prefix and write a recruit-log row for each of them, which is noise at
    /// best and confusing at worst. The window is generous enough to cover the
    /// real case (a recruit whose rank went unrecorded for a day or two) and
    /// tight enough to exclude everyone else.
    /// </summary>
    private async Task RunRecruitSideEffectsAsync(
        SocketGuildUser member, string rank, List<string> rankRoles)
    {
        if (!rank.Equals("RCT", StringComparison.OrdinalIgnoreCase)) return;

        var windowHours = Math.Max(0, _config.RankReconcileSideEffectWindowHours);
        var joinedAt = member.JoinedAt?.UtcDateTime;
        var recentJoin = joinedAt is not null
                         && DateTime.UtcNow - joinedAt.Value <= TimeSpan.FromHours(windowHours);

        if (!recentJoin)
        {
            _logger.LogInformation(
                "Rank reconcile: recorded {Username} ({UserId}) as RCT but skipped the nickname prefix and " +
                "recruit log, because they joined {JoinedAt:u}, outside the {Hours}h window",
                member.Username, member.Id, joinedAt, windowHours);
            return;
        }

        try
        {
            var newNickname = await _rankHandler.TryApplyRctNicknameAsync(member, rankRoles);
            var recruitName = !string.IsNullOrWhiteSpace(newNickname) ? newNickname! : member.DisplayName;
            await _rankHandler.TryLogRecruitAsync(recruitName, member);
        }
        catch (Exception ex)
        {
            // The RankHistory row is already saved and correct; these are the
            // trimmings. Never let them undo the repair.
            _logger.LogError(ex,
                "Rank reconcile: recorded {Username} ({UserId}) but the recruit side effects failed",
                member.Username, member.Id);
        }
    }

    /// <summary>
    /// Best-effort answer to "when did they actually get this rank". See the
    /// class summary for why inventing this badly is worse than being vague.
    /// </summary>
    private async Task<(DateTime AssignedAt, string Source)> ResolveAssignedAtAsync(
        SocketGuild guild, SocketGuildUser member, string rank)
    {
        var now = DateTime.UtcNow;
        var floor = now.AddDays(-Math.Max(1, _config.RankReconcileMaxBackdateDays));

        // 1. The audit-log entry that added the role. Exact, when it is still
        //    inside Discord's 45 day retention.
        try
        {
            var added = await _roleAudit.FindRoleAdditionAsync(guild, member.Id, rank, floor);
            if (added is not null && added.ChangedAtUtc >= floor)
                return (added.ChangedAtUtc, "audit log");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Rank reconcile: audit lookup failed while dating {UserId}, falling back", member.Id);
        }

        // 2. Their join timestamp. Right for the case this service exists for,
        //    a member who arrived already holding the role.
        if (member.JoinedAt?.UtcDateTime is DateTime joined && joined >= floor)
            return (joined, "join timestamp");

        // 3. Now. Deliberately the last resort and deliberately not backdated:
        //    a member whose rank is older than the backdate floor would other
        //    wise be handed all the events since as credit toward their next
        //    promotion.
        return (now, "reconcile time");
    }

    // ── Notice ───────────────────────────────────────────────────────────

    private async Task PostRepairNoticeAsync(
        SocketGuild guild, SocketGuildUser member, ReconcileOutcome outcome, string context)
    {
        var channelId = _config.RankReconcileNoticeChannelId;
        if (channelId == 0) return;

        if (guild.GetTextChannel(channelId) is not SocketTextChannel channel)
        {
            _logger.LogWarning(
                "RankReconcileNoticeChannelId {ChannelId} did not resolve in {Guild}, repair notice not posted",
                channelId, guild.Name);
            return;
        }

        try
        {
            var eb = new EmbedBuilder()
                .WithColor(Color.Gold)
                .WithTitle("Rank recorded after the fact")
                .WithDescription(
                    $"{member.Mention} was holding **{outcome.Rank}** with nothing recorded against it, so the bot "
                    + $"never saw the role arrive when {context}. It has been recorded now, and their timeline, "
                    + "promotion clock and recruit log are back in step.")
                .AddField("Member", member.DisplayName, true)
                .AddField("Rank", outcome.Rank ?? "unknown", true)
                .AddField("Dated", $"<t:{new DateTimeOffset(DateTime.SpecifyKind(outcome.AssignedAt, DateTimeKind.Utc)).ToUnixTimeSeconds()}:f> ({outcome.DateSource})")
                .WithFooter("Run /timeline with role history on to see what the audit log says happened.")
                .WithCurrentTimestamp();

            await channel.SendMessageAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not post the rank reconcile notice for {UserId}", member.Id);
        }
    }
}
