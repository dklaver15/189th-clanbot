using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Retention feature — LIVE path. Captures member departures in real time and
/// classifies them as voluntary leave vs. officer kick vs. bot AWOL kick vs.
/// account-age-gate ban vs. manual ban. Writes one MemberDeparture row per
/// departure event (DepartureDetection = "Live").
///
/// ── Why this exists ──
/// Discord's UserLeft event carries no reason — voluntary leave, kick, and
/// ban all surface identically. The only signal for "why" is a guild
/// audit-log entry (Kick / Ban) for the same user. UserLeft and
/// AuditLogCreated are separate gateway events that arrive in EITHER order,
/// so we correlate them with a short-lived in-memory cache plus a grace
/// window (finalized by DepartureClassificationWorker).
///
/// Departures that happen while the bot is OFFLINE are not seen here (Discord
/// doesn't replay UserLeft); MemberRosterReconciler recovers those.
///
/// ── Classification rules ── (see DepartureFactory.ClassifyKickBan)
///   actor == bot && Kick → "KickedAwol"        (AWOL is the only bot KICK path)
///   actor == bot && Ban  → "KickedAccountAge"  if reason starts with "Account-age",
///                          "Banned"             otherwise
///   actor != bot && Kick → "Kicked"            (manual officer kick)
///   actor != bot && Ban  → "Banned"            (manual officer ban)
///   no audit entry in grace window → "Left"    (voluntary; set by the worker)
///
/// ── Failure handling ──
/// Everything is fire-and-forget and wrapped in try/catch — a DB hiccup or a
/// missing audit-log permission must never crash the gateway. A missed audit
/// correlation just leaves the row "Left", the safe default (we under-count
/// kicks rather than inventing them).
///
/// ── Permissions / intents ──
/// Requires the GuildBans gateway intent (already enabled for
/// AuditLogWatcherHandler) and the View Audit Log guild permission, or
/// AuditLogCreated never fires and everything falls back to "Left".
/// </summary>
public class DepartureCaptureHandler
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<DepartureCaptureHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Breadcrumbs for the AuditLogCreated-arrives-before-UserLeft race.
    /// Keyed by departing user ID. Consumed by CaptureAsync; swept by
    /// DepartureClassificationWorker once older than the grace window.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, PendingKick> _pendingKicks = new();

    public DepartureCaptureHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<DepartureCaptureHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>A kick/ban audit entry observed before its matching UserLeft arrived.</summary>
    private sealed record PendingKick(
        string Classification,
        ulong ActorId,
        string ActorName,
        string? Reason,
        DateTime ObservedAt);

    public TimeSpan GraceWindow =>
        TimeSpan.FromSeconds(Math.Max(15, _config.RetentionClassificationGraceSeconds));

    /// <summary>Exposed so the classification worker can sweep stale breadcrumbs.</summary>
    public void EvictStalePendingKicks(DateTime nowUtc)
    {
        foreach (var kvp in _pendingKicks)
        {
            if (nowUtc - kvp.Value.ObservedAt > GraceWindow)
                _pendingKicks.TryRemove(kvp.Key, out _);
        }
    }

    public void Register(DiscordSocketClient client)
    {
        client.UserLeft += OnUserLeft;
        client.AuditLogCreated += OnAuditLogCreated;
    }

    // ── UserLeft ─────────────────────────────────────────────────────────

    private Task OnUserLeft(SocketGuild guild, SocketUser user)
    {
        _ = CaptureAsync(guild, user);
        return Task.CompletedTask;
    }

    private async Task CaptureAsync(SocketGuild guild, SocketUser user)
    {
        try
        {
            var departedAt = DateTime.UtcNow;

            // Live member may still be cached at leave time — grab rank/roles
            // from it directly so we never read the RankHistory row that
            // MemberLifecycleHandler is about to delete on this same event.
            var member = guild.GetUser(user.Id);

            var roleNames = member is null
                ? Array.Empty<string>()
                : member.Roles.Where(r => !r.IsEveryone).Select(r => r.Name).ToArray();

            var rolesSnapshot = string.Join(", ", roleNames);
            var rankList = _config.GetRankRolesList();
            var rank = DepartureFactory.ResolveHighestRank(roleNames, rankList);
            var wasGuest = roleNames.Any(n => n.Equals("Guest", StringComparison.OrdinalIgnoreCase));
            var hadReserve = !string.IsNullOrWhiteSpace(_config.ReserveRoleName)
                          && roleNames.Any(n => n.Equals(_config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));
            var displayName = member?.DisplayName ?? user.GlobalName ?? user.Username;

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var (joinedAt, joinedAtSource) = await DepartureFactory.ResolveJoinedAtAsync(
                db, guild.Id, member?.JoinedAt?.UtcDateTime, user.Id);
            var tenureDays = DepartureFactory.TenureDays(joinedAt, departedAt);

            var (messagesLifetime, eventsLifetime) =
                await DepartureFactory.GetEngagementAsync(db, guild.Id, user.Id);
            var joinSourceLabel = await DepartureFactory.GetJoinSourceLabelAsync(db, guild.Id, user.Id);

            var isRejoin = await db.MemberDepartures
                .AnyAsync(d => d.GuildId == guild.Id && d.UserId == user.Id);

            var row = new MemberDeparture
            {
                GuildId = guild.Id,
                UserId = user.Id,
                Username = user.Username,
                DisplayName = displayName,
                DepartedAt = departedAt,
                JoinedAt = joinedAt,
                JoinedAtSource = joinedAtSource,
                TenureDays = tenureDays,
                DepartureDetection = "Live",
                RankAtDeparture = rank,
                RolesAtDeparture = rolesSnapshot,
                WasGuest = wasGuest,
                HadReserve = hadReserve,
                MessagesLifetime = messagesLifetime,
                EventsAttendedLifetime = eventsLifetime,
                IsRejoin = isRejoin,
                JoinSourceLabel = joinSourceLabel,
                CreatedAt = departedAt,
                Classification = "Pending",
            };

            // Did a Kick/Ban audit entry already arrive for this user?
            if (_pendingKicks.TryRemove(user.Id, out var pending)
                && departedAt - pending.ObservedAt <= GraceWindow)
            {
                row.Classification = pending.Classification;
                row.ActorId = pending.ActorId == 0UL ? null : pending.ActorId;
                row.ActorName = pending.ActorName;
                row.Reason = pending.Reason;
                row.ClassifiedAt = departedAt;
            }

            db.MemberDepartures.Add(row);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Captured departure: {Display} ({UserId}) classified={Class} rank={Rank} " +
                "tenure={Tenure} (src={Src}) rejoin={Rejoin}",
                displayName, user.Id, row.Classification, rank,
                tenureDays?.ToString("F1") ?? "unknown", joinedAtSource, isRejoin);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to capture departure for {Username} ({UserId}) in guild {GuildId}",
                user.Username, user.Id, guild.Id);
        }
    }

    // ── AuditLogCreated (Kick / Ban only) ────────────────────────────────

    private Task OnAuditLogCreated(SocketAuditLogEntry entry, SocketGuild guild)
    {
        if (entry.Action is not (ActionType.Kick or ActionType.Ban))
            return Task.CompletedTask;

        _ = ClassifyFromAuditAsync(entry, guild);
        return Task.CompletedTask;
    }

    private async Task ClassifyFromAuditAsync(SocketAuditLogEntry entry, SocketGuild guild)
    {
        try
        {
            var observedAt = DateTime.UtcNow;
            var actorId = entry.User?.Id;
            var actorIsBot = actorId is not null && actorId.Value == _client.CurrentUser.Id;
            var reason = entry.Reason;

            // Ban/Kick payloads sometimes arrive untyped on the gateway
            // (observed for Ban) — upgrade via REST, mirroring
            // AuditLogWatcherHandler.ResolveDataAsync.
            var targetId = await ResolveTargetIdAsync(entry, guild);
            if (targetId is null)
            {
                _logger.LogDebug(
                    "Departure classifier: could not resolve target for audit entry {EntryId} ({Action})",
                    entry.Id, entry.Action);
                return;
            }

            var classification = DepartureFactory.ClassifyKickBan(actorIsBot, entry.Action, reason);
            var actorName = entry.User?.Username ?? (actorIsBot ? "ClanGuard" : "Unknown");

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var cutoff = observedAt - GraceWindow;
            var row = await db.MemberDepartures
                .Where(d => d.GuildId == guild.Id
                         && d.UserId == targetId.Value
                         && d.DepartedAt >= cutoff)
                .OrderByDescending(d => d.DepartedAt)
                .FirstOrDefaultAsync();

            if (row is not null && (row.Classification == "Pending" || row.Classification == "Left"))
            {
                row.Classification = classification;
                row.ActorId = actorId;
                row.ActorName = actorName;
                row.Reason = reason;
                row.ClassifiedAt = observedAt;
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "Reconciled departure {DepartureId} for {UserId} → {Class} (actor {Actor})",
                    row.Id, targetId.Value, classification, actorName);
            }
            else
            {
                // UserLeft hasn't arrived yet (or a ban without a leave, e.g.
                // banning someone who already left). Leave a breadcrumb for
                // CaptureAsync; the worker evicts it if no leave follows.
                _pendingKicks[targetId.Value] = new PendingKick(
                    classification, actorId ?? 0UL, actorName, reason, observedAt);

                _logger.LogDebug(
                    "Departure classifier: stashed {Class} breadcrumb for {UserId} (no leave row yet)",
                    classification, targetId.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Departure classifier failed for audit entry {EntryId} in guild {GuildId}",
                entry.Id, guild.Id);
        }
    }

    /// <summary>
    /// Extracts the target user ID from a Kick/Ban audit entry, upgrading the
    /// gateway payload to its typed shape via REST when necessary.
    /// </summary>
    private async Task<ulong?> ResolveTargetIdAsync(SocketAuditLogEntry entry, SocketGuild guild)
    {
        var id = DepartureFactory.TargetIdFrom(entry.Data);
        if (id is not null) return id;

        try
        {
            await foreach (var page in guild.GetAuditLogsAsync(limit: 10, actionType: entry.Action))
            {
                foreach (var rest in page)
                {
                    if (rest.Id == entry.Id)
                        return DepartureFactory.TargetIdFrom(rest.Data);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Departure classifier: REST fallback failed for entry {EntryId} ({Action})",
                entry.Id, entry.Action);
        }
        return null;
    }
}
