using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace ClanGuardBot.Services;

/// <summary>
/// In-memory snapshot of "uses per invite code" for the guild, kept fresh by
/// InviteAttributionService. The cache exists because Discord doesn't tell us
/// in real time which invite a new member used — we have to derive it by
/// comparing the post-join use counts against a pre-join snapshot. This class
/// is the snapshot store.
///
/// ── Concurrency ──
/// Reads and writes can happen from multiple gateway events at once
/// (UserJoined, InviteCreated, InviteDeleted, plus the slash-command path
/// when /invite create writes a fresh row). ConcurrentDictionary handles the
/// per-key updates; the vanity slot is guarded by its own simple lock since
/// it's a single int.
///
/// ── Vanity URL is tracked separately ──
/// SocketGuild.GetInvitesAsync() does NOT include the vanity invite. It has
/// to be fetched via GetVanityInviteAsync. Rather than mix it into the same
/// dictionary (and risk a code collision with a regular invite code that
/// happens to match the vanity slug), we keep its use count in a separate
/// field with its own accessors.
///
/// ── This class doesn't subscribe to Discord events ──
/// It's a pure state container. InviteAttributionService owns the gateway
/// subscriptions and calls into this class to read/write. Keeping these
/// concerns separated makes the attribution logic testable in isolation
/// from Discord wiring.
/// </summary>
public sealed class InviteCacheService
{
    private readonly ConcurrentDictionary<string, int> _useCountByCode = new();
    private readonly object _vanityLock = new();
    private int? _vanityUses;
    private bool _hydrated;

    private readonly ILogger<InviteCacheService> _logger;

    public InviteCacheService(ILogger<InviteCacheService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// True once HydrateAsync has completed at least once. Until then, the
    /// attribution service should treat any UserJoined as Unattributed
    /// rather than guessing — the cache has no baseline to diff against.
    /// </summary>
    public bool IsHydrated => _hydrated;

    /// <summary>
    /// Pull the full invite list (plus vanity) from Discord and populate the
    /// cache. Safe to call again after a reconnect; existing entries get
    /// overwritten with fresh counts and any codes Discord has since dropped
    /// are removed.
    /// </summary>
    public async Task HydrateAsync(SocketGuild guild)
    {
        try
        {
            var invites = await guild.GetInvitesAsync();
            var observedCodes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var invite in invites)
            {
                _useCountByCode[invite.Code] = invite.Uses ?? 0;
                observedCodes.Add(invite.Code);
            }

            // Drop stale entries — codes that were in the cache but Discord
            // no longer reports. A reconnect after a long disconnect could
            // leave us holding revoked codes; this prunes them.
            foreach (var staleCode in _useCountByCode.Keys.Except(observedCodes).ToList())
            {
                _useCountByCode.TryRemove(staleCode, out _);
            }

            // Vanity — separate API.
            try
            {
                var vanity = await guild.GetVanityInviteAsync();
                lock (_vanityLock)
                {
                    _vanityUses = vanity?.Uses ?? null;
                }
            }
            catch (Exception ex)
            {
                // Guild may not have a vanity URL configured (requires Boost level 3).
                // That's fine — we just leave _vanityUses null and skip vanity attribution.
                _logger.LogDebug(ex, "Vanity invite fetch returned no result for {Guild}", guild.Name);
                lock (_vanityLock)
                {
                    _vanityUses = null;
                }
            }

            _hydrated = true;
            _logger.LogInformation(
                "Invite cache hydrated for {Guild}: {InviteCount} regular invite(s), vanity uses = {VanityUses}",
                guild.Name, _useCountByCode.Count,
                _vanityUses.HasValue ? _vanityUses.Value.ToString() : "n/a");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hydrate invite cache for {Guild}", guild.Name);
            // Don't flip _hydrated true on failure — the next attribution attempt
            // will fall through to "Unattributed" rather than diff against an
            // incomplete baseline. A subsequent successful hydrate can recover.
        }
    }

    /// <summary>
    /// Returns a frozen snapshot of (code → uses) for diff-time comparison.
    /// The dictionary returned is a new copy; callers may iterate freely
    /// without worrying about concurrent mutation.
    /// </summary>
    public IReadOnlyDictionary<string, int> Snapshot()
    {
        return _useCountByCode.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Current vanity use count, or null if the guild has no vanity URL.</summary>
    public int? VanityUses
    {
        get
        {
            lock (_vanityLock) return _vanityUses;
        }
    }

    /// <summary>
    /// Replace the cached count for a single code. Used after attribution to
    /// roll the cache forward, and on InviteCreated when a new code appears.
    /// </summary>
    public void SetUses(string code, int uses)
    {
        _useCountByCode[code] = uses;
    }

    /// <summary>Replace the cached vanity count. Used after attribution to vanity.</summary>
    public void SetVanityUses(int? uses)
    {
        lock (_vanityLock) _vanityUses = uses;
    }

    /// <summary>Drop a code from the cache. Called from InviteDeleted.</summary>
    public void Remove(string code)
    {
        _useCountByCode.TryRemove(code, out _);
    }

    /// <summary>
    /// Bulk-update from a fresh fetch result. Called after each UserJoined
    /// once attribution has been written, so the next join diffs against
    /// the post-join state rather than the pre-join state.
    /// </summary>
    public void ReplaceAll(IEnumerable<(string Code, int Uses)> current, int? vanityUses)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (code, uses) in current)
        {
            _useCountByCode[code] = uses;
            seen.Add(code);
        }
        foreach (var staleCode in _useCountByCode.Keys.Except(seen).ToList())
        {
            _useCountByCode.TryRemove(staleCode, out _);
        }

        lock (_vanityLock) _vanityUses = vanityUses;
    }
}
