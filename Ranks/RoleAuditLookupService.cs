using Discord;
using Discord.Rest;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Reads member role changes back out of Discord's own server audit log.
///
/// ── Why this exists ──
/// The bot records that a rank changed but never who changed it. RankChange
/// has no actor column, RankTrackingHandler's log lines name only the member,
/// and AuditLogWatcherHandler subscribes to channel, ban, bot, guild and role
/// events but not MemberRoleUpdated. So the answer to "who took this member's
/// rank away" has only ever lived in Discord's audit log, which on mobile is a
/// short scrolling window with no per-member filter. This service is the read
/// path that was missing.
///
/// ── Why it scans instead of filtering ──
/// The userId parameter on GetAuditLogsAsync filters by the ACTOR, not the
/// target, so there is no server-side way to ask for "everything that happened
/// to this member". We page MemberRoleUpdated entries newest-first and match
/// the target ourselves, exactly as MemberRosterReconciler does for kicks and
/// bans. Bounded by <see cref="MaxPages"/> pages and by the caller's cutoff,
/// and the result says plainly when a bound was hit rather than presenting a
/// truncated scan as a complete one.
///
/// ── Retention ──
/// Discord stores audit entries for 45 days and deletes them after that. A
/// lookup that finds nothing therefore means "no entry within retention", never
/// "it did not happen". Callers should say so rather than asserting a negative.
/// </summary>
public class RoleAuditLookupService
{
    /// <summary>Discord's own audit-log retention. Nothing older is fetchable.</summary>
    public const int AuditRetentionDays = 45;

    /// <summary>Entries per REST page. 100 is Discord's maximum.</summary>
    private const int PageLimit = 100;

    /// <summary>
    /// Hard bound on pages fetched per lookup. Five pages is 500 role-change
    /// entries, which on this server reaches back well past the point where a
    /// member's own history stops being interesting. Exists so a lookup on a
    /// quiet member cannot walk the entire 45 days one page at a time.
    /// </summary>
    private const int MaxPages = 5;

    private readonly DiscordSocketClient _client;
    private readonly ILogger<RoleAuditLookupService> _logger;

    public RoleAuditLookupService(
        DiscordSocketClient client,
        ILogger<RoleAuditLookupService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Every role change recorded against one member, newest first.
    /// </summary>
    /// <param name="guild">Guild whose audit log to read.</param>
    /// <param name="targetUserId">The member the changes were performed on.</param>
    /// <param name="notBeforeUtc">
    /// Stop scanning at this point. Defaults to the full 45 days of retention.
    /// Pass something tighter when you only care about a recent change: it is
    /// the difference between one page and five.
    /// </param>
    /// <param name="maxChanges">Stop after this many matches.</param>
    public async Task<RoleAuditResult> GetRoleChangesAsync(
        SocketGuild guild,
        ulong targetUserId,
        DateTime? notBeforeUtc = null,
        int maxChanges = 15,
        CancellationToken ct = default)
    {
        var cutoff = notBeforeUtc ?? DateTime.UtcNow.AddDays(-AuditRetentionDays);
        var changes = new List<RoleAuditChange>();
        var pages = 0;
        var boundHit = false;
        DateTime? oldestScanned = null;

        try
        {
            await foreach (var page in guild
                .GetAuditLogsAsync(limit: PageLimit, actionType: ActionType.MemberRoleUpdated)
                .WithCancellation(ct))
            {
                pages++;
                var stop = false;

                foreach (var entry in page)
                {
                    var changedAt = SnowflakeUtils.FromSnowflake(entry.Id).UtcDateTime;
                    oldestScanned = changedAt;

                    // Entries come newest-first, so the first one older than the
                    // cutoff means everything after it is older too.
                    if (changedAt < cutoff) { stop = true; break; }

                    if (entry.Data is not MemberRoleAuditLogData data) continue;
                    if (data.Target?.Id != targetUserId) continue;

                    var added   = data.Roles.Where(r => r.Added).Select(r => r.Name).ToList();
                    var removed = data.Roles.Where(r => r.Removed).Select(r => r.Name).ToList();
                    if (added.Count == 0 && removed.Count == 0) continue;

                    var actorId = entry.User?.Id;
                    changes.Add(new RoleAuditChange(
                        ChangedAtUtc:   changedAt,
                        ActorId:        actorId,
                        ActorName:      entry.User?.Username ?? "Unknown",
                        ActorIsClanGuard: actorId is not null
                                          && _client.CurrentUser is not null
                                          && actorId == _client.CurrentUser.Id,
                        Added:          added,
                        Removed:        removed,
                        Reason:         string.IsNullOrWhiteSpace(entry.Reason) ? null : entry.Reason));

                    if (changes.Count >= maxChanges) { stop = true; boundHit = true; break; }
                }

                if (stop) break;

                if (pages >= MaxPages)
                {
                    boundHit = true;
                    _logger.LogDebug(
                        "Role audit lookup for {UserId} stopped at the {Pages}-page bound; " +
                        "older changes may exist back to {Oldest:u}",
                        targetUserId, MaxPages, oldestScanned);
                    break;
                }
            }
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(
                "Cannot read the audit log in {Guild}: the bot is missing the View Audit Log permission",
                guild.Name);
            return RoleAuditResult.Failed(RoleAuditStatus.PermissionDenied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Role audit lookup failed for {UserId} in {Guild}", targetUserId, guild.Name);
            return RoleAuditResult.Failed(RoleAuditStatus.Failed);
        }

        return new RoleAuditResult(changes, RoleAuditStatus.Ok, boundHit, oldestScanned);
    }

    /// <summary>
    /// The most recent change that REMOVED the named role from the member, or
    /// null if there is none within the scanned range. Null is "nothing found in
    /// what we could see", not proof it never happened.
    /// </summary>
    public async Task<RoleAuditChange?> FindRoleRemovalAsync(
        SocketGuild guild,
        ulong targetUserId,
        string roleName,
        DateTime? notBeforeUtc = null,
        CancellationToken ct = default)
    {
        var result = await GetRoleChangesAsync(guild, targetUserId, notBeforeUtc, maxChanges: 15, ct);
        return result.Changes.FirstOrDefault(c =>
            c.Removed.Any(r => r.Equals(roleName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The most recent change that ADDED the named role to the member, or null
    /// if there is none within the scanned range. Used to date a rank the bot
    /// failed to observe live, so the repaired record carries the real
    /// timestamp instead of the moment we noticed.
    /// </summary>
    public async Task<RoleAuditChange?> FindRoleAdditionAsync(
        SocketGuild guild,
        ulong targetUserId,
        string roleName,
        DateTime? notBeforeUtc = null,
        CancellationToken ct = default)
    {
        var result = await GetRoleChangesAsync(guild, targetUserId, notBeforeUtc, maxChanges: 15, ct);
        return result.Changes.FirstOrDefault(c =>
            c.Added.Any(r => r.Equals(roleName, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>One audit-log entry: who changed which of a member's roles, and when.</summary>
public sealed record RoleAuditChange(
    DateTime ChangedAtUtc,
    ulong? ActorId,
    string ActorName,
    bool ActorIsClanGuard,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    string? Reason);

/// <summary>Why a lookup returned what it did.</summary>
public enum RoleAuditStatus
{
    /// <summary>The scan ran. Changes may still be empty, which means none were found in range.</summary>
    Ok,

    /// <summary>The bot lacks View Audit Log, so nothing could be read at all.</summary>
    PermissionDenied,

    /// <summary>The scan threw. Treat as "unknown", never as "nothing happened".</summary>
    Failed,
}

/// <summary>
/// Result of a role-audit lookup. <see cref="BoundHit"/> matters: when true the
/// scan stopped at a cap rather than at the end of the member's history, so the
/// caller must say the list is partial instead of presenting it as everything.
/// </summary>
public sealed record RoleAuditResult(
    IReadOnlyList<RoleAuditChange> Changes,
    RoleAuditStatus Status,
    bool BoundHit,
    DateTime? OldestScannedUtc)
{
    public static RoleAuditResult Failed(RoleAuditStatus status) =>
        new(Array.Empty<RoleAuditChange>(), status, false, null);

    /// <summary>True when the scan itself worked, whatever it found.</summary>
    public bool Succeeded => Status == RoleAuditStatus.Ok;
}
