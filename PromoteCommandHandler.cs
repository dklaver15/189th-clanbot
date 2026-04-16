using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /promote slash command. Delegates the actual role swap,
/// nickname update, and announcement to <see cref="PromotionService"/> so
/// this command and the nightly AutoPromotionService share one code path.
/// </summary>
public class PromoteCommandHandler
{
    private readonly ILogger<PromoteCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly PromotionService _promotion;

    public PromoteCommandHandler(
        ILogger<PromoteCommandHandler> logger,
        IOptions<BotConfig> config,
        PromotionService promotion)
    {
        _logger = logger;
        _config = config.Value;
        _promotion = promotion;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not "promote")
            return;

        try
        {
            await HandlePromote(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /promote");
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandlePromote(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        // Permission check — caller must have Manage Roles or be in an exempt role
        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasPromotePermission(caller))
        {
            await command.FollowupAsync("❌ You don't have permission to use this command.", ephemeral: true);
            return;
        }

        // Parse options
        var rankOption = command.Data.Options.FirstOrDefault(o => o.Name == "rank");
        var memberOption = command.Data.Options.FirstOrDefault(o => o.Name == "member");

        var rank = rankOption?.Value?.ToString() ?? string.Empty;
        var member = memberOption?.Value as SocketGuildUser;

        if (member is null)
        {
            await command.FollowupAsync("❌ Could not find that member.", ephemeral: true);
            return;
        }

        // Validate rank using the shared RankMap
        if (!PromotionService.RankMap.ContainsKey(rank))
        {
            var validRanks = string.Join(", ", PromotionService.RankMap.Keys.Select(k => $"`{k}`"));
            await command.FollowupAsync($"❌ **Invalid rank**: `{rank}`. Valid ranks: {validRanks}", ephemeral: true);
            return;
        }

        // Prevent self-promotion
        if (member.Id == command.User.Id)
        {
            await command.FollowupAsync("❌ You cannot promote yourself.", ephemeral: true);
            return;
        }

        // ── Delegate to the shared service ──────────────────────────
        var result = await _promotion.PromoteAsync(member, rank);

        if (!result.Success)
        {
            await command.FollowupAsync($"❌ {result.Error}", ephemeral: true);
            return;
        }

        _logger.LogInformation(
            "Manual promotion: {Member} promoted to {NewRank} by {Promoter}",
            member.Username, result.NewRankName, caller.Username);

        if (!result.NicknameUpdated)
        {
            await command.FollowupAsync(
                $"✅ **Promotion Confirmed**: {member.Mention} → **{result.NewRankName}**\n" +
                $"⚠️ Could not update nickname (insufficient permissions). " +
                $"Please update manually to: `{result.NewNickname}`");
            return;
        }

        await command.FollowupAsync(
            $"✅ **Promotion Confirmed**: {result.NewNickname}\n" +
            $"{member.Mention} has been promoted to **{result.NewRankName}**.");
    }

    private bool HasPromotePermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minRank = _config.PromoteDemoteMinRank;
        var minIndex = rankRoles.FindIndex(r => r.Equals(minRank, StringComparison.OrdinalIgnoreCase));

        if (minIndex < 0) return false;

        // Check if the user has any rank at or above the minimum
        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }
}