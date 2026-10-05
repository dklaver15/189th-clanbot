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
/// Retention feature — OFFLINE-GAP path. Self-healing recovery for departures
/// the live DepartureCaptureHandler can't see because Discord never replays
/// UserLeft after a reconnect.
///
/// ── How it works ──
/// Maintains a persisted roster of present members (KnownMembers), refreshed
/// on UserJoined and on each periodic sweep. On startup (after a grace delay)
/// and every RetentionRosterReconcileHours, it diffs the persisted roster
/// against the live guild:
///   • members present now            → upsert KnownMember (refresh LastSeen,
///                                       cache JoinedAt + rank/roles)
///   • KnownMembers no longer present  → they left while we weren't watching;
///                                       record a MemberDeparture
///                                       (DepartureDetection = "Reconciled")
///                                       unless the live path already captured
///                                       it, then drop the KnownMember row.
///
/// Reconciled departures are back-classified from the REST audit log (one
/// bounded fetch per sweep, shared across all missing members) within
/// Discord's ~45-day retention; anything older or unattributable settles to
/// "Left". Because rank/roles and Discord's JoinedAt were cached WHILE the
/// member was present, a reconciled row still reports exact tenure and rank.
///
/// ── Baseline note ──
/// The FIRST sweep after this feature deploys just populates KnownMembers with
/// the current roster (no departures detected — we can't retroactively know
/// who left before we started tracking). Offline-departure detection is
/// effective from the second sweep onward, or for anyone who joins (and is
/// recorded via UserJoined) after the baseline.
///
/// ── Failure handling ──
/// All fire-and-forget / try-caught. Mirrors the orphan-reconciliation shape
/// of VoiceSessionCleanupService.
/// </summary>
public class MemberRosterReconciler : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    // Bounds on the per-sweep audit-log scan (newest-first). Plenty for a
    // normal sweep; we also stop early once entries predate the oldest
    // missing member's LastSeen.
    private const int AuditPageLimit = 100;
    private const int MaxAuditPagesPerAction = 3;
    private static readonly TimeSpan AuditRetention = TimeSpan.FromDays(44);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<MemberRosterReconciler> _logger;
    private readonly BotConfig _config;

    public MemberRosterReconciler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<MemberRosterReconciler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    private TimeSpan ReconcileInterval =>
        TimeSpan.FromHours(Math.Max(1, _config.RetentionRosterReconcileHours));

    public void Register(DiscordSocketClient client)
    {
        client.UserJoined += OnUserJoined;
    }

    // ── UserJoined: keep the roster fresh in real time ───────────────────

    private Task OnUserJoined(SocketGuildUser member)
    {
        if (member.IsBot) return Task.CompletedTask;
        _ = UpsertOnJoinAsync(member);
        return Task.CompletedTask;
    }

    private async Task UpsertOnJoinAsync(SocketGuildUser member)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var rankList = _config.GetRankRolesList();
            await UpsertKnownMemberAsync(db, member, rankList, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Roster reconciler: failed to record join for {User} ({UserId})",
                member.Username, member.Id);
        }
    }

    // ── Periodic + startup sweep ─────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "MemberRosterReconciler started; sweeping on startup and every {Hours}h",
            Math.Max(1, _config.RetentionRosterReconcileHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MemberRosterReconciler sweep failed; will retry next interval");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var guild = _client.Guilds.FirstOrDefault();
        if (guild is null)
        {
            _logger.LogWarning("Roster reconciler: no guild connected — skipping sweep");
            return;
        }

        // Ensure the member cache is complete before we treat "not present" as
        // "departed" — otherwise an incomplete cache would manufacture
        // phantom departures.
        await guild.DownloadUsersAsync();

        var currentMembers = guild.Users.Where(u => !u.IsBot).ToList();
        var currentIds = currentMembers.Select(u => u.Id).ToHashSet();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var rankList = _config.GetRankRolesList();
        var now = DateTime.UtcNow;

        var known = await db.KnownMembers
            .Where(k => k.GuildId == guild.Id)
            .ToListAsync(ct);
        var knownById = known.ToDictionary(k => k.UserId);

        // ── Pass 1: upsert everyone currently present ──
        foreach (var member in currentMembers)
        {
            UpsertKnownMember(db, knownById, member, rankList, now);
        }

        // ── Pass 2: anyone we knew but who is no longer present has departed ──
        var departed = known.Where(k => !currentIds.Contains(k.UserId)).ToList();

        if (departed.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogDebug(
                "Roster reconciler: {Present} present, no offline departures detected",
                currentMembers.Count);
            return;
        }

        // One bounded audit-log fetch shared across all missing members.
        var oldestLastSeen = departed.Min(k => k.LastSeenUtc);
        var auditSince = oldestLastSeen < now - AuditRetention ? now - AuditRetention : oldestLastSeen;
        var auditByTarget = await FetchRecentKickBansAsync(guild, auditSince);

        var reconciledCount = 0;
        var alreadyCaptured = 0;

        foreach (var km in departed)
        {
            // Did the live path already capture this departure?
            var captured = await db.MemberDepartures.AnyAsync(
                d => d.GuildId == guild.Id
                  && d.UserId == km.UserId
                  && d.DepartedAt >= km.LastSeenUtc, ct);

            if (captured)
            {
                db.KnownMembers.Remove(km);
                alreadyCaptured++;
                continue;
            }

            var (joinedAt, joinedAtSource) = await DepartureFactory.ResolveJoinedAtAsync(
                db, guild.Id, km.JoinedAtCached, km.UserId, ct);
            var tenureDays = DepartureFactory.TenureDays(joinedAt, now);
            var (messages, events) = await DepartureFactory.GetEngagementAsync(db, guild.Id, km.UserId, ct);
            var joinSourceLabel = await DepartureFactory.GetJoinSourceLabelAsync(db, guild.Id, km.UserId, ct);

            var roleNames = string.IsNullOrWhiteSpace(km.RolesCached)
                ? Array.Empty<string>()
                : km.RolesCached.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var wasGuest = roleNames.Any(n => n.Equals("Guest", StringComparison.OrdinalIgnoreCase));
            var hadReserve = !string.IsNullOrWhiteSpace(_config.ReserveRoleName)
                          && roleNames.Any(n => n.Equals(_config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));

            // Back-classify from the audit log; default to voluntary "Left".
            var classification = "Left";
            ulong? actorId = null;
            string? actorName = null;
            string? reason = null;
            if (auditByTarget.TryGetValue(km.UserId, out var hit))
            {
                var actorIsBot = hit.ActorId is not null && hit.ActorId.Value == _client.CurrentUser.Id;
                classification = DepartureFactory.ClassifyKickBan(actorIsBot, hit.Action, hit.Reason);
                actorId = hit.ActorId;
                actorName = hit.ActorName;
                reason = hit.Reason;
            }

            var isRejoin = await db.MemberDepartures.AnyAsync(
                d => d.GuildId == guild.Id && d.UserId == km.UserId, ct);

            db.MemberDepartures.Add(new MemberDeparture
            {
                GuildId = guild.Id,
                UserId = km.UserId,
                Username = km.Username,
                DisplayName = string.IsNullOrWhiteSpace(km.DisplayName) ? km.Username : km.DisplayName,
                DepartedAt = now,                 // detection time — bot was offline at actual departure
                JoinedAt = joinedAt,
                JoinedAtSource = joinedAtSource,
                TenureDays = tenureDays,
                DepartureDetection = "Reconciled",
                Classification = classification,
                ActorId = actorId,
                ActorName = actorName,
                Reason = reason,
                RankAtDeparture = km.RankCached,
                RolesAtDeparture = km.RolesCached,
                WasGuest = wasGuest,
                HadReserve = hadReserve,
                MessagesLifetime = messages,
                EventsAttendedLifetime = events,
                IsRejoin = isRejoin,
                JoinSourceLabel = joinSourceLabel,
                CreatedAt = now,
                ClassifiedAt = now,
            });

            db.KnownMembers.Remove(km);
            reconciledCount++;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Roster reconciler: {Present} present; recovered {Reconciled} offline departure(s), " +
            "{Captured} already captured live",
            currentMembers.Count, reconciledCount, alreadyCaptured);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void UpsertKnownMember(
        BotDbContext db,
        Dictionary<ulong, KnownMember> knownById,
        SocketGuildUser member,
        List<string> rankList,
        DateTime now)
    {
        var roleNames = member.Roles.Where(r => !r.IsEveryone).Select(r => r.Name).ToArray();
        var rolesCsv = string.Join(", ", roleNames);
        var rank = DepartureFactory.ResolveHighestRank(roleNames, rankList);

        if (knownById.TryGetValue(member.Id, out var existing))
        {
            existing.Username = member.Username;
            existing.DisplayName = member.DisplayName ?? member.Username;
            existing.RankCached = rank;
            existing.RolesCached = rolesCsv;
            existing.LastSeenUtc = now;
            if (member.JoinedAt is not null)
                existing.JoinedAtCached = member.JoinedAt.Value.UtcDateTime;
        }
        else
        {
            var row = new KnownMember
            {
                GuildId = member.Guild.Id,
                UserId = member.Id,
                Username = member.Username,
                DisplayName = member.DisplayName ?? member.Username,
                JoinedAtCached = member.JoinedAt?.UtcDateTime,
                RankCached = rank,
                RolesCached = rolesCsv,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            };
            db.KnownMembers.Add(row);
            knownById[member.Id] = row;
        }
    }

    /// <summary>Single-member upsert used by the UserJoined hook.</summary>
    private async Task UpsertKnownMemberAsync(
        BotDbContext db, SocketGuildUser member, List<string> rankList, DateTime now)
    {
        var existing = await db.KnownMembers
            .FirstOrDefaultAsync(k => k.GuildId == member.Guild.Id && k.UserId == member.Id);
        var map = new Dictionary<ulong, KnownMember>();
        if (existing is not null) map[member.Id] = existing;
        UpsertKnownMember(db, map, member, rankList, now);
    }

    /// <summary>
    /// Fetches recent Kick + Ban audit entries (newest-first, bounded) into a
    /// target-user → classification-input lookup. Stops scanning an action once
    /// entries predate <paramref name="since"/>. First write per target wins
    /// (newest entry), which is what we want for "why did they leave".
    /// </summary>
    private async Task<Dictionary<ulong, AuditHit>> FetchRecentKickBansAsync(
        SocketGuild guild, DateTime since)
    {
        var result = new Dictionary<ulong, AuditHit>();

        foreach (var action in new[] { ActionType.Kick, ActionType.Ban })
        {
            try
            {
                var pages = 0;
                await foreach (var page in guild.GetAuditLogsAsync(limit: AuditPageLimit, actionType: action))
                {
                    if (++pages > MaxAuditPagesPerAction) break;

                    var stop = false;
                    foreach (var entry in page)
                    {
                        var createdAt = SnowflakeUtils.FromSnowflake(entry.Id).UtcDateTime;
                        if (createdAt < since) { stop = true; break; }

                        var targetId = DepartureFactory.TargetIdFrom(entry.Data);
                        if (targetId is null) continue;

                        var actorIsBot = entry.User?.Id == _client.CurrentUser.Id;
                        var actorName = entry.User?.Username ?? (actorIsBot ? "ClanGuard" : "Unknown");

                        // Keep the newest entry per target across both actions.
                        if (result.TryGetValue(targetId.Value, out var prev) && prev.CreatedAt >= createdAt)
                            continue;

                        result[targetId.Value] = new AuditHit(
                            action, entry.User?.Id, actorName, entry.Reason, createdAt);
                    }

                    if (stop) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Roster reconciler: audit fetch failed for {Action}; reconciled rows for affected users will settle to 'Left'",
                    action);
            }
        }

        return result;
    }

    private sealed record AuditHit(
        ActionType Action,
        ulong? ActorId,
        string ActorName,
        string? Reason,
        DateTime CreatedAt);
}
