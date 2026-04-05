using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /promote slash command — removes the old rank role,
/// adds the new rank role, and updates the member's nickname.
/// </summary>
public class PromoteCommandHandler
{
    private readonly ILogger<PromoteCommandHandler> _logger;
    private readonly BotConfig _config;

    /// <summary>
    /// Rank progression chain — each key is the shorthand typed by the user,
    /// mapping to (previous rank role name to remove, new rank role name to add).
    /// </summary>
    private static readonly Dictionary<string, (string OldRole, string NewRole)> RankMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "pvt", ("RCT", "PVT") },
            { "pfc", ("PVT", "PFC") },
            { "spc", ("PFC", "SPC") },
            { "cpl", ("SPC", "CPL") },
            { "sgt", ("CPL", "SGT") },
            { "ssg", ("SGT", "SSG") },
            { "sfc", ("SSG", "SFC") },
            { "msg", ("SFC", "MSG") },
            { "1sg", ("MSG", "1SG") },
            { "sgm", ("1SG", "SGM") },
            { "csm", ("SGM", "CSM") },
            { "sma", ("CSM", "SMA") },
        };

    /// <summary>SMA is a special case: can be promoted from either SGM or CSM.</summary>
    private static readonly HashSet<string> SmaSourceRoles =
        new(StringComparer.OrdinalIgnoreCase) { "SGM", "CSM" };

    public PromoteCommandHandler(
        ILogger<PromoteCommandHandler> logger,
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

        // Validate rank
        if (!RankMap.TryGetValue(rank, out var rankInfo))
        {
            var validRanks = string.Join(", ", RankMap.Keys.Select(k => $"`{k}`"));
            await command.FollowupAsync($"❌ **Invalid rank**: `{rank}`. Valid ranks: {validRanks}", ephemeral: true);
            return;
        }

        // Prevent self-promotion
        if (member.Id == command.User.Id)
        {
            await command.FollowupAsync("❌ You cannot promote yourself.", ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null) return;

        // Verify the new role exists in the server
        var newRole = guild.Roles.FirstOrDefault(r => r.Name == rankInfo.NewRole);
        if (newRole is null)
        {
            await command.FollowupAsync(
                $"❌ Could not find role **{rankInfo.NewRole}** in this server.", ephemeral: true);
            return;
        }

        // Check if member already has the new rank
        if (member.Roles.Any(r => r.Name == rankInfo.NewRole))
        {
            await command.FollowupAsync(
                $"❌ {member.Mention} already has the **{rankInfo.NewRole}** role.", ephemeral: true);
            return;
        }

        // ── Remove old role(s) ──────────────────────────────────────
        var removedRoles = new List<string>();

        if (rank.Equals("sma", StringComparison.OrdinalIgnoreCase))
        {
            // SMA special case: remove either SGM or CSM (or both)
            foreach (var sourceName in SmaSourceRoles)
            {
                var sourceRole = member.Roles.FirstOrDefault(r => r.Name == sourceName);
                if (sourceRole is not null)
                {
                    await member.RemoveRoleAsync(sourceRole);
                    removedRoles.Add(sourceName);
                }
            }
        }
        else
        {
            var oldRole = member.Roles.FirstOrDefault(r => r.Name == rankInfo.OldRole);
            if (oldRole is not null)
            {
                await member.RemoveRoleAsync(oldRole);
                removedRoles.Add(rankInfo.OldRole);
            }
        }

        // ── Add new role ────────────────────────────────────────────
        await member.AddRoleAsync(newRole);

        // ── Update nickname ─────────────────────────────────────────
        // Preserve the member's display name by stripping any existing rank prefix.
        // Nickname format is "RANK.DisplayName", so we look for that pattern.
        // Also handles legacy "RANK . DisplayName" with spaces.
        var currentName = member.DisplayName;
        var rankRoles = _config.GetRankRolesList();

        var baseName = currentName;
        foreach (var rankRole in rankRoles)
        {
            // Match patterns like "SGT . Dogteem" or "SGT. Dogteem" or "SGT.Dogteem"
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
                $"✅ **Promotion Confirmed**: {member.Mention} → **{rankInfo.NewRole}**\n" +
                $"⚠️ Could not update nickname (insufficient permissions). " +
                $"Please update manually to: `{newNickname}`");
            return;
        }

        // ── Success ─────────────────────────────────────────────────
        var removedInfo = removedRoles.Count > 0
            ? $"Removed: {string.Join(", ", removedRoles)}"
            : "No previous rank role found";

        _logger.LogInformation(
            "Promotion: {Member} promoted to {NewRank} by {Promoter}. {RemovedInfo}",
            member.Username, rankInfo.NewRole, caller.Username, removedInfo);

        await command.FollowupAsync(
            $"✅ **Promotion Confirmed**: {rankInfo.NewRole}.{baseName}\n" +
            $"{member.Mention} has been promoted to **{rankInfo.NewRole}**.");
    }

    private bool HasPromotePermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;

        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}