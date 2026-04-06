using ClanGuardBot.Data;
using ClanGuardBot.Models;
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
/// </summary>
public class RankTrackingHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RankTrackingHandler> _logger;
    private readonly BotConfig _config;

    public RankTrackingHandler(
        IServiceProvider services,
        ILogger<RankTrackingHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
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
                // First time seeing a rank for this user
                db.RankHistories.Add(new RankHistory
                {
                    GuildId = guildId,
                    UserId = userId,
                    RankName = afterRank,
                    AssignedAt = DateTime.UtcNow
                });
                _logger.LogInformation(
                    "Rank assigned: {Username} → {NewRank} in {Guild}",
                    after.Username, afterRank, after.Guild.Name);
            }
            else
            {
                // Rank changed — update with exact timestamp
                _logger.LogInformation(
                    "Rank change: {Username} {OldRank} → {NewRank} in {Guild}",
                    after.Username, rankRecord.RankName, afterRank, after.Guild.Name);
                rankRecord.RankName = afterRank;
                rankRecord.AssignedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();

            // ── Auto-prefix nickname when RCT role is gained ──────────
            // This handles new members who accept rules via MEE6 and get the RCT role.
            // Checks if RCT was added in this update (not present before, present after).
            var rctGained = after.Roles.Any(r => r.Name.Equals("RCT", StringComparison.OrdinalIgnoreCase)) &&
                           !before.Roles.Any(r => r.Name.Equals("RCT", StringComparison.OrdinalIgnoreCase));

            if (rctGained)
            {
                try
                {
                    var displayName = after.DisplayName;

                    // Don't prefix if they already have a rank prefix
                    var hasPrefix = rankRoles.Any(r =>
                        displayName.StartsWith($"{r}.", StringComparison.OrdinalIgnoreCase) ||
                        displayName.StartsWith($"{r} . ", StringComparison.OrdinalIgnoreCase));

                    if (!hasPrefix)
                    {
                        var newNickname = $"RCT.{displayName}";
                        await after.ModifyAsync(p => p.Nickname = newNickname);
                        _logger.LogInformation(
                            "Auto-nickname: {Username} → {NewNick} in {Guild}",
                            after.Username, newNickname, after.Guild.Name);
                    }
                }
                catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning(
                        "Could not set RCT nickname for {Username}: insufficient permissions",
                        after.Username);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error tracking rank change for {Username} in {Guild}",
                after.Username, after.Guild.Name);
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