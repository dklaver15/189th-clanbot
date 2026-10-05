using System.Collections.Concurrent;
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
/// ── Second job: the RCT role / name mismatch notice ──
/// This service also watches for the only two states a human actually needs to
/// act on: a member holding the RCT role whose name has no "RCT." prefix, and a
/// member whose name carries the prefix without the role. Everything else is
/// either correct or simply someone who has not accepted the rules yet. See
/// CheckRctNameMismatchAsync.
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

    /// <summary>
    /// Members already reported as mismatched, and which way round the mismatch
    /// was. Stops the 12-hourly sweep from re-posting the same unresolved
    /// mismatch every cycle, while still re-posting if it flips to the other
    /// direction. Cleared the moment the member's role and name agree again.
    ///
    /// In memory on purpose: no table, no migration. A restart therefore
    /// re-announces anything still outstanding, which is the right behaviour for
    /// a notice that means "somebody needs to fix this".
    /// </summary>
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), string> _reportedMismatches = new();

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
                // Log only. A repair is routine (see BotConfig.RctMismatchNoticeEnabled
                // for why it is not worth a channel post), and the notice that
                // does get posted is the mismatch check below.
                _logger.LogInformation(
                    "Rank reconcile (join): repaired {Username} ({UserId}) as {Rank}, dated {AssignedAt:u} from {Source}. " +
                    "No live role-change event was seen for them",
                    member.Username, member.Id, outcome.Rank, outcome.AssignedAt, outcome.DateSource);
            }

            // Runs whether or not anything was repaired, and whether or not they
            // are ranked: a member who has not accepted the rules has neither the
            // role nor the prefix, which agree, so they are silently fine.
            await CheckRctNameMismatchAsync(guild, member);
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

        var members = guild.Users.Where(u => !u.IsBot).ToList();

        // Mismatch pass first, and before any DbContext exists. It is outside the
        // dry-run gate because it only reads and posts, so a dry run has nothing
        // to hold back; and it is outside the scope because each unreported
        // mismatch waits to confirm itself, and holding a context open across all
        // of that would be pure waste.
        foreach (var member in members)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await CheckRctNameMismatchAsync(guild, member);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Rank reconcile sweep: mismatch check failed on {Username} ({UserId})",
                    member.Username, member.Id);
            }
        }

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

        var candidates = members
            .Where(u => !tracked.Contains(u.Id))
            .Where(u => RankTrackingHandler.GetHighestRank(u.Roles.Select(r => r.Name), rankRoles) is not null)
            .ToList();

        if (candidates.Count == 0)
        {
            // Information, not Debug: production runs at Information, so a Debug
            // line here would make "everything is in order" and "the sweep never
            // ran" look identical in the log. The whole point of the sweep is
            // that someone can check it.
            _logger.LogInformation(
                "Rank reconcile sweep: every ranked member in {Guild} has a RankHistory row, nothing to repair",
                guild.Name);
            return;
        }

        _logger.LogInformation(
            "Rank reconcile sweep: {Count} member(s) in {Guild} hold a rank role with no RankHistory row{Mode}",
            candidates.Count, guild.Name, dryRun ? " (DRY RUN, nothing will be written)" : string.Empty);

        var actioned = 0;
        foreach (var member in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var outcome = await ReconcileMemberAsync(
                    db, guild, member, rankRoles,
                    dryRun: dryRun,
                    allowSideEffects: !dryRun);

                // Dry run reports what it would have done, so the tally has to
                // count those too or the summary reads "0 of 12 would be
                // repaired", which is the opposite of what it means.
                if (outcome.Repaired || outcome.WouldRepair) actioned++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Rank reconcile sweep: failed on {Username} ({UserId})", member.Username, member.Id);
            }
        }

        _logger.LogInformation(
            "Rank reconcile sweep finished: {Actioned} of {Count} {Verb}",
            actioned, candidates.Count, dryRun ? "would be repaired" : "repaired");
    }

    // ── Shared repair ────────────────────────────────────────────────────

    /// <summary>
    /// Result of looking at one member. <see cref="Repaired"/> is false for a
    /// member who needed nothing, and also for a dry run that only reported.
    /// </summary>
    private sealed record ReconcileOutcome(
        bool Repaired,
        bool WouldRepair,
        string? Rank,
        DateTime AssignedAt,
        string DateSource);

    private static readonly ReconcileOutcome NoAction =
        new(false, false, null, default, string.Empty);

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
            return new ReconcileOutcome(false, true, rank, assignedAt, dateSource);
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

        return new ReconcileOutcome(true, false, rank, assignedAt, dateSource);
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

    // ── Mismatch notice ──────────────────────────────────────────────────

    /// <summary>
    /// The RCT role and the "RCT." name prefix should always travel together.
    /// This posts when they do not, in either direction, and nothing else.
    ///
    /// ── Why these two states and no others ──
    /// A member with neither is not broken: they joined and have not accepted
    /// the rules, so they have no rank and no prefix, and that is a normal way
    /// to sit in the server. A member with both is correct no matter which code
    /// path got them there. Only the two disagreements need a human:
    ///
    ///   • role, no prefix — MEE6 gave them RCT but the rename never happened,
    ///     so they read as unranked everywhere people actually look.
    ///   • prefix, no role — the rename stuck but the role is gone, so they read
    ///     as a recruit while counting as unranked for the AWOL window. This is
    ///     what an un-toggled rules reaction leaves behind.
    ///
    /// ── Why it re-reads before reporting ──
    /// /promote and /demote add the new rank role, strip the old one, then
    /// rename, so a member passes through both mismatch states for a moment
    /// during an ordinary promotion. Confirming after a short wait is what keeps
    /// every promotion from posting two false notices.
    ///
    /// Reports each member once per direction until it resolves, so an
    /// unfixed mismatch does not repost every sweep.
    /// </summary>
    private async Task CheckRctNameMismatchAsync(SocketGuild guild, SocketGuildUser member)
    {
        if (!_config.RctMismatchNoticeEnabled || _config.RctMismatchNoticeChannelId == 0) return;
        if (member.IsBot) return;

        var key = (guild.Id, member.Id);
        var state = DescribeMismatch(member);

        if (state is null)
        {
            // Role and name agree. Forget them, so if it breaks again later they
            // get reported again rather than being suppressed forever.
            _reportedMismatches.TryRemove(key, out _);
            return;
        }

        // Same mismatch we already reported: stay quiet.
        if (_reportedMismatches.TryGetValue(key, out var reported) && reported == state.Kind)
            return;

        // Re-read after a pause to rule out a promotion in flight.
        await Task.Delay(TimeSpan.FromSeconds(MismatchConfirmSeconds));

        var current = guild.GetUser(member.Id);
        if (current is null)
        {
            _reportedMismatches.TryRemove(key, out _);
            return;
        }

        var confirmed = DescribeMismatch(current);
        if (confirmed is null || confirmed.Kind != state.Kind)
        {
            _reportedMismatches.TryRemove(key, out _);
            return;
        }

        // Claim it before posting, atomically, so the join check and a sweep
        // cannot both report the same member when they overlap. Whoever loses
        // the swap stays quiet.
        if (_reportedMismatches.TryGetValue(key, out var existing))
        {
            if (existing == confirmed.Kind) return;
            if (!_reportedMismatches.TryUpdate(key, confirmed.Kind, existing)) return;
        }
        else if (!_reportedMismatches.TryAdd(key, confirmed.Kind))
        {
            return;
        }

        if (guild.GetTextChannel(_config.RctMismatchNoticeChannelId) is not SocketTextChannel channel)
        {
            _logger.LogWarning(
                "RctMismatchNoticeChannelId {ChannelId} did not resolve in {Guild}, mismatch notice for {UserId} not posted",
                _config.RctMismatchNoticeChannelId, guild.Name, member.Id);
            return;
        }

        try
        {
            var eb = new EmbedBuilder()
                .WithColor(Color.Orange)
                .WithTitle(confirmed.Title)
                .WithDescription($"{current.Mention} {confirmed.Description}")
                .AddField("Name", current.DisplayName, true)
                .AddField("Has RCT role", HasRctRole(current) ? "Yes" : "No", true)
                .AddField("Name has RCT. prefix", HasRctPrefix(current) ? "Yes" : "No", true)
                .WithFooter($"User ID: {current.Id}")
                .WithCurrentTimestamp();

            await channel.SendMessageAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);

            _logger.LogInformation(
                "RCT mismatch: {Username} ({UserId}) in {Guild} — {Kind}",
                current.Username, current.Id, guild.Name, confirmed.Kind);
        }
        catch (Exception ex)
        {
            // Let go of the claim so the next sweep tries again rather than
            // treating a failed post as reported.
            _reportedMismatches.TryRemove(key, out _);
            _logger.LogWarning(ex, "Could not post the RCT mismatch notice for {UserId}", member.Id);
        }
    }

    /// <summary>Seconds to wait and re-check before reporting a mismatch.</summary>
    private const int MismatchConfirmSeconds = 20;

    private sealed record MismatchState(string Kind, string Title, string Description);

    /// <summary>
    /// Null when the RCT role and the "RCT." prefix agree, whether both are
    /// present or both absent.
    /// </summary>
    private static MismatchState? DescribeMismatch(SocketGuildUser member)
    {
        var hasRole = HasRctRole(member);
        var hasPrefix = HasRctPrefix(member);

        if (hasRole == hasPrefix) return null;

        return hasRole
            ? new MismatchState(
                "role-without-prefix",
                "RCT role, but no RCT. in their name",
                "has the RCT role, but their server name was never given the **RCT.** prefix. "
                + "Anyone reading the member list sees them as unranked.")
            : new MismatchState(
                "prefix-without-role",
                "RCT. in their name, but no RCT role",
                "has **RCT.** in their server name but does not hold the RCT role. "
                + "They read as a recruit while counting as unranked for the AWOL window.");
    }

    /// <summary>
    /// The role name is the literal "RCT", matching RankTrackingHandler's own
    /// checks and the prefix it builds. Kept literal rather than configurable so
    /// all three stay in step.
    /// </summary>
    private static bool HasRctRole(SocketGuildUser member) =>
        member.Roles.Any(r => r.Name.Equals("RCT", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mirrors the tolerance in RankTrackingHandler.TryApplyRctNicknameAsync,
    /// which accepts a spaced "RCT . " as an existing prefix, so a name that
    /// handler would leave alone is not reported here as missing one.
    /// </summary>
    private static bool HasRctPrefix(SocketGuildUser member)
    {
        var name = member.DisplayName ?? string.Empty;
        return name.StartsWith("RCT.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("RCT . ", StringComparison.OrdinalIgnoreCase);
    }
}
