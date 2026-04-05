using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /demote slash command — removes the current rank role,
/// adds the lower rank role, and updates the member's nickname.
/// </summary>
public class DemoteCommandHandler
{
    private readonly ILogger<DemoteCommandHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Demotion map — each key is the rank to demote TO,
    /// mapping to (current rank role to remove, new lower rank role to add).
    /// </summary>
    private static readonly Dictionary<string, (string OldRole, string NewRole)> DemoteMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "rct", ("PVT", "RCT") },
            { "pvt", ("PFC", "PVT") },
            { "pfc", ("SPC", "PFC") },
            { "spc", ("CPL", "SPC") },
            { "cpl", ("SGT", "CPL") },
            { "sgt", ("SSG", "SGT") },
            { "ssg", ("SFC", "SSG") },
            { "sfc", ("MSG", "SFC") },
            { "msg", ("1SG", "MSG") },
            { "1sg", ("SGM", "1SG") },
            { "sgm", ("CSM", "SGM") },
            { "csm", ("SMA", "CSM") },
        };

    public DemoteCommandHandler(
        ILogger<DemoteCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not "demote")
            return;

        try
        {
            await HandleDemote(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /demote");
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleDemote(SocketSlashCommand command)
    {
        await command.DeferAsync();

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        // Permission check
        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasDemotePermission(caller))
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

        // Validate rank
        if (!DemoteMap.TryGetValue(rank, out var rankInfo))
        {
            var validRanks = string.Join(", ", DemoteMap.Keys.Select(k => $"`{k}`"));
            await command.FollowupAsync($"❌ **Invalid rank**: `{rank}`. Valid ranks: {validRanks}", ephemeral: true);
            return;
        }

        // Prevent self-demotion
        if (member.Id == command.User.Id)
        {
            await command.FollowupAsync("❌ You cannot demote yourself.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        // Verify the new (lower) role exists in the server
        var newRole = guild.Roles.FirstOrDefault(r => r.Name == rankInfo.NewRole);
        if (newRole is null)
        {
            await command.FollowupAsync(
                $"❌ Could not find role **{rankInfo.NewRole}** in this server.", ephemeral: true);
            return;
        }

        // Check if member already has the target rank
        if (member.Roles.Any(r => r.Name == rankInfo.NewRole))
        {
            await command.FollowupAsync(
                $"❌ {member.Mention} already has the **{rankInfo.NewRole}** role.", ephemeral: true);
            return;
        }

        // ── Remove current (higher) role ────────────────────────────
        var removedRoles = new List<string>();
        var oldRole = member.Roles.FirstOrDefault(r => r.Name == rankInfo.OldRole);
        if (oldRole is not null)
        {
            await member.RemoveRoleAsync(oldRole);
            removedRoles.Add(rankInfo.OldRole);
        }

        // ── Add new (lower) role ────────────────────────────────────
        await member.AddRoleAsync(newRole);

        // ── Update nickname ─────────────────────────────────────────
        var currentName = member.DisplayName;
        var rankRoles = _config.GetRankRolesList();

        var baseName = currentName;
        foreach (var rankRole in rankRoles)
        {
            var prefix = $"{rankRole} . ";
            if (currentName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                baseName = currentName[prefix.Length..];
                break;
            }

            var prefixNoDotSpace = $"{rankRole}.";
            if (currentName.StartsWith(prefixNoDotSpace, StringComparison.OrdinalIgnoreCase))
            {
                baseName = currentName[prefixNoDotSpace.Length..].TrimStart();
                break;
            }
        }

        var newNickname = $"{rankInfo.NewRole}.{baseName}";

        try
        {
            await member.ModifyAsync(p => p.Nickname = newNickname);
        }
        catch (Discord.Net.HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Could not update nickname for {User}: {Error}",
                member.Username, ex.Message);

            await command.FollowupAsync(
                $"⬇️ **Demotion Confirmed**: {member.Mention} → **{rankInfo.NewRole}**\n" +
                $"⚠️ Could not update nickname (insufficient permissions). " +
                $"Please update manually to: `{newNickname}`");
            return;
        }

        // ── Success ─────────────────────────────────────────────────
        var removedInfo = removedRoles.Count > 0
            ? $"Removed: {string.Join(", ", removedRoles)}"
            : "No previous rank role found";

        _logger.LogInformation(
            "Demotion: {Member} demoted to {NewRank} by {Demoter}. {RemovedInfo}",
            member.Username, rankInfo.NewRole, caller.Username, removedInfo);

        await command.FollowupAsync(
            $"⬇️ **Demotion Confirmed**: {rankInfo.NewRole}.{baseName}\n" +
            $"{member.Mention} has been demoted to **{rankInfo.NewRole}**.");
    }

    private bool HasDemotePermission(SocketGuildUser user)
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