using System.Collections.Concurrent;
using ClanGuardBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Reads the Discord to Sleeper links and keeps them in memory, so rendering a
/// standings table does not mean a database round trip per team.
///
/// ── Why a cache at all ──
/// Every fantasy surface needs the same map (Sleeper user id to Discord user id) to
/// decide whether a team line gets an @mention. The live scoreboard rebuilds that
/// table on every cycle while games are on. The links themselves change a handful
/// of times a season, so re-reading them on every render would be pure waste.
///
/// The cache is invalidated explicitly by the link command rather than expiring on
/// a timer, so a member who just linked sees the effect on their next command
/// instead of waiting out a TTL. The TTL below is a backstop for anything that
/// edits the table without going through the command.
/// </summary>
public sealed class SleeperLinkService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<SleeperLinkService> _logger;

    private readonly ConcurrentDictionary<ulong, Entry> _cache = new();

    public SleeperLinkService(IServiceProvider services, ILogger<SleeperLinkService> logger)
    {
        _services = services;
        _logger = logger;
    }

    private sealed record Entry(IReadOnlyDictionary<string, ulong> Map, DateTime ExpiresUtc);

    /// <summary>
    /// Sleeper user id to Discord user id for one guild. Empty when nobody has
    /// linked, which every caller treats as "render plain names".
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ulong>> GetSleeperToDiscordAsync(ulong guildId)
    {
        if (_cache.TryGetValue(guildId, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
            return cached.Map;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var map = await db.SleeperLinks
                .Where(l => l.GuildId == guildId)
                .ToDictionaryAsync(l => l.SleeperUserId, l => l.DiscordUserId);

            _cache[guildId] = new Entry(map, DateTime.UtcNow.Add(Ttl));
            return map;
        }
        catch (Exception ex)
        {
            // A failed read degrades to plain names rather than failing the surface
            // that asked. Nobody's standings should break because the links table
            // was momentarily unreadable.
            _logger.LogWarning(ex, "Sleeper link lookup failed for guild {GuildId}; rendering without mentions", guildId);
            return cached?.Map ?? new Dictionary<string, ulong>();
        }
    }

    /// <summary>Drops the cached map for a guild. Called after any link change.</summary>
    public void Invalidate(ulong guildId) => _cache.TryRemove(guildId, out _);
}
