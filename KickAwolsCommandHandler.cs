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
/// Handles the /kick-awols slash command. Iterates over every guild member with
/// the AWOL role and removes them from the server. Includes:
///
///   • Reserve guard — anyone with the role named in BotConfig.ReserveRoleName
///     is skipped, even if they somehow ended up flagged AWOL. AwolCheckService
///     already prevents Reserve members from being assigned AWOL via the
///     ExemptRoles list, but we re-check here in case the Reserve role was
///     applied AFTER they were marked AWOL (manual role change, role-on-rejoin
///     race, etc.). This is the belt-and-suspenders defense the user explicitly
///     asked for.
///
///   • Min-rank protection — members at or above BotConfig.AwolKickMinRank are
///     skipped to prevent a tracking bug or rogue invocation from nuking
///     leadership. Same rank gate that controls who can RUN the command.
///
///   • Role hierarchy check — Discord won't let the bot kick anyone whose top
///     role sits at or above the bot's top role; we detect that up front so
///     we can write a "SkippedHierarchy" audit row instead of letting
///     KickAsync throw.
///
///   • Best-effort DM before kick — sent inside a try/catch since DMs may be
///     closed. Gives the kicked member a clear reason and an invitation to
///     come back if they want.
///
///   • Confirm/dry-run flags — must pass exactly one of confirm:true or
///     dry-run:true. No default action, no implicit go-ahead. Dry run writes
///     the same audit rows but skips the actual kick.
///
///   • Per-member audit row — every member processed (kicked, skipped, or
///     failed) gets an AwolKickAuditRecord row. Discord's audit log only
///     retains 45 days; this preserves history forever.
/// </summary>
public class KickAwolsCommandHandler
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly ILogger<KickAwolsCommandHandler> _logger;
    private readonly BotConfig _config;

    public KickAwolsCommandHandler(
        DiscordSocketClient client,
        IServiceProvider services,
        ILogger<KickAwolsCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _client = client;
        _services = services;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Hooks the slash-command listener. Call once during bot startup
    /// (same Register pattern as the other command handlers).
    /// </summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandExecuted;
    }

    private async Task OnSlashCommandExecuted(SocketSlashCommand cmd)
    {
        if (cmd.Data.Name != "kick-awols") return;

        // Defer ephemeral so only the invoker sees progress / results.
        await cmd.DeferAsync(ephemeral: true);

        try
        {
            await HandleKickAwolsAsync(cmd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in /kick-awols");
            await cmd.FollowupAsync($"❌ Unexpected error: {ex.Message}", ephemeral: true);
        }
    }

    private async Task HandleKickAwolsAsync(SocketSlashCommand cmd)
    {
        var guild = (cmd.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await cmd.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        // ------------------------------------------------------------------
        // Permission check — must meet the configured minimum rank
        // ------------------------------------------------------------------
        var invoker = guild.GetUser(cmd.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await cmd.FollowupAsync(
                $"⛔ You need to be **{_config.AwolKickMinRank}** or higher to use this command.",
                ephemeral: true);
            return;
        }

        // ------------------------------------------------------------------
        // Mode flags — must pass exactly one of confirm or dry-run
        // ------------------------------------------------------------------
        var confirm = (bool)(cmd.Data.Options.FirstOrDefault(o => o.Name == "confirm")?.Value ?? false);
        var dryRun  = (bool)(cmd.Data.Options.FirstOrDefault(o => o.Name == "dry-run")?.Value ?? false);

        if (!confirm && !dryRun)
        {
            await cmd.FollowupAsync(
                "ℹ️ Pass `confirm:true` to actually kick AWOL members, " +
                "or `dry-run:true` to preview without kicking. " +
                "**Strongly recommend running with `dry-run:true` first.**",
                ephemeral: true);
            return;
        }

        if (confirm && dryRun)
        {
            await cmd.FollowupAsync(
                "ℹ️ Pass either `confirm:true` OR `dry-run:true`, not both.",
                ephemeral: true);
            return;
        }

        // ------------------------------------------------------------------
        // Resolve roles
        // ------------------------------------------------------------------
        var awolRole = guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, _config.AwolRoleName, StringComparison.OrdinalIgnoreCase));
        if (awolRole is null)
        {
            await cmd.FollowupAsync(
                $"❌ AWOL role `{_config.AwolRoleName}` not found in this server.",
                ephemeral: true);
            return;
        }

        // Reserve role may legitimately be missing in a fresh server setup — that's
        // fine, just means no one currently holds it. Log a warning so it's visible
        // but proceed normally.
        var reserveRole = guild.Roles.FirstOrDefault(r =>
            string.Equals(r.Name, _config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));
        if (reserveRole is null)
        {
            _logger.LogWarning(
                "Reserve role '{ReserveRole}' not found in guild {Guild}. " +
                "Reserve guard will be skipped (no Reserve members to protect).",
                _config.ReserveRoleName, guild.Name);
        }

        // ------------------------------------------------------------------
        // Make sure the user cache is populated before enumerating
        // ------------------------------------------------------------------
        await guild.DownloadUsersAsync();

        var awolMembers = guild.Users
            .Where(u => !u.IsBot)
            .Where(u => u.Roles.Any(r => r.Id == awolRole.Id))
            .ToList();

        if (awolMembers.Count == 0)
        {
            await cmd.FollowupAsync("✅ No AWOL members found. Nothing to do.", ephemeral: true);
            return;
        }

        // Bot's top role position determines who we can kick at all
        var botTopRolePos = guild.CurrentUser.Roles.Max(r => r.Position);

        // ------------------------------------------------------------------
        // Process each AWOL member
        // ------------------------------------------------------------------
        var kicked       = new List<string>();
        var skippedRes   = new List<string>();
        var skippedRank  = new List<string>();
        var skippedHier  = new List<string>();
        var failed       = new List<string>();
        var auditRows    = new List<AwolKickAuditRecord>();

        var kickReason = $"AWOL — automated removal via /kick-awols by {cmd.User.Username}";
        var rankList   = _config.GetRankRolesList();
        var minRankIdx = rankList.FindIndex(r =>
            r.Equals(_config.AwolKickMinRank, StringComparison.OrdinalIgnoreCase));

        foreach (var member in awolMembers)
        {
            var rolesSnapshot = string.Join(", ",
                member.Roles.Where(r => !r.IsEveryone).Select(r => r.Name));

            var audit = new AwolKickAuditRecord
            {
                GuildId      = guild.Id,
                UserId       = member.Id,
                Username     = member.Username,
                DisplayName  = member.DisplayName ?? member.Username,
                RolesAtKick  = rolesSnapshot,
                ProcessedAt  = DateTime.UtcNow,
                InvokerId    = cmd.User.Id,
                InvokerName  = cmd.User.Username,
                WasDryRun    = dryRun,
                Reason       = kickReason
            };

            // ── Guard 1: Reserve members are NEVER kicked ─────────────────
            // The user explicitly requested this re-check inside the kick
            // command. AwolCheckService should already prevent Reserve members
            // from being assigned AWOL, but verify here in case the Reserve
            // role was added between AWOL assignment and kick (manual role
            // change, role-on-rejoin race, etc.).
            if (reserveRole != null && member.Roles.Any(r => r.Id == reserveRole.Id))
            {
                audit.Outcome = "SkippedReserve";
                auditRows.Add(audit);
                skippedRes.Add(member.DisplayName);
                _logger.LogInformation(
                    "Skipped AWOL kick for {User} — has Reserve role (Reserve guard)",
                    member.Username);
                continue;
            }

            // ── Guard 2: Don't kick members at/above protected leadership rank ─
            if (minRankIdx >= 0 && MemberRankIsAtOrAbove(member, rankList, minRankIdx))
            {
                audit.Outcome = "SkippedProtected";
                auditRows.Add(audit);
                skippedRank.Add(member.DisplayName);
                _logger.LogWarning(
                    "Skipped AWOL kick for {User} — at/above protected rank {MinRank}. " +
                    "This shouldn't happen; investigate why they were marked AWOL.",
                    member.Username, _config.AwolKickMinRank);
                continue;
            }

            // ── Guard 3: Discord role hierarchy ───────────────────────────
            // Bot must outrank target or KickAsync will throw. Skip up front
            // so we get a clean audit row instead of an exception.
            var memberTopRolePos = member.Roles.Max(r => r.Position);
            if (memberTopRolePos >= botTopRolePos)
            {
                audit.Outcome = "SkippedHierarchy";
                auditRows.Add(audit);
                skippedHier.Add(member.DisplayName);
                _logger.LogWarning(
                    "Skipped AWOL kick for {User} — top role above bot's top role",
                    member.Username);
                continue;
            }

            // ── Dry run: log intended kick and move on ────────────────────
            if (dryRun)
            {
                audit.Outcome = "DryRunKicked";
                auditRows.Add(audit);
                kicked.Add(member.DisplayName);
                continue;
            }

            // ── Best-effort DM before the kick ────────────────────────────
            try
            {
                var dm = await member.CreateDMChannelAsync();
                await dm.SendMessageAsync(
                    "You've been removed from the **189th** for inactivity (AWOL).\n\n" +
                    "If you'd like to come back, reach out to a member of leadership and " +
                    "we'll get you reinstated. No hard feelings — life happens.");
            }
            catch
            {
                // DMs disabled / closed — fine, continue with the kick.
            }

            // ── Actually kick ─────────────────────────────────────────────
            try
            {
                await member.KickAsync(
                    reason: kickReason,
                    options: new RequestOptions { AuditLogReason = kickReason });

                audit.Outcome = "Kicked";
                auditRows.Add(audit);
                kicked.Add(member.DisplayName);
                _logger.LogInformation(
                    "Kicked {User} ({UserId}) for AWOL — invoker: {Invoker}",
                    member.Username, member.Id, cmd.User.Username);

                // Gentle pacing to avoid hitting Discord's rate limit on bulk kicks.
                // 500ms is conservative; Discord's documented kick rate limit is
                // 5/5s per guild but burst behavior varies.
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                audit.Outcome      = "Failed";
                audit.ErrorMessage = ex.Message;
                auditRows.Add(audit);
                failed.Add($"{member.DisplayName} ({ex.Message})");
                _logger.LogError(ex,
                    "Failed to kick {User} ({UserId}) for AWOL",
                    member.Username, member.Id);
            }
        }

        // ------------------------------------------------------------------
        // Persist audit rows
        // ------------------------------------------------------------------
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.AwolKickAudits.AddRange(auditRows);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to persist AWOL kick audit rows. {Count} rows lost.",
                auditRows.Count);
        }

        // ------------------------------------------------------------------
        // Build the summary response
        // ------------------------------------------------------------------
        var modeLabel = dryRun ? "**DRY RUN** — no members were actually kicked" : "**LIVE RUN**";
        var verb      = dryRun ? "Would kick" : "Kicked";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"### AWOL Kick Summary — {modeLabel}");
        sb.AppendLine();
        sb.AppendLine($"**{verb}: {kicked.Count}**");
        if (kicked.Count > 0)
            sb.AppendLine(TruncateList(kicked));

        if (skippedRes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — Reserve role: {skippedRes.Count}**");
            sb.AppendLine(TruncateList(skippedRes));
        }

        if (skippedRank.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — at/above {_config.AwolKickMinRank}: {skippedRank.Count}**");
            sb.AppendLine(TruncateList(skippedRank));
        }

        if (skippedHier.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped — role hierarchy: {skippedHier.Count}**");
            sb.AppendLine(TruncateList(skippedHier));
            sb.AppendLine("_(Bot's top role must be above the member's top role to kick them.)_");
        }

        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Failed: {failed.Count}**");
            sb.AppendLine(TruncateList(failed));
        }

        sb.AppendLine();
        sb.AppendLine($"_Audit rows written: {auditRows.Count}_");

        // Discord caps message content at 2000 chars; truncate if we somehow exceed
        var summary = sb.ToString();
        if (summary.Length > 1900)
            summary = summary.Substring(0, 1900) + "\n…(truncated)";

        await cmd.FollowupAsync(summary, ephemeral: true);
    }

    /// <summary>
    /// True if the invoker has Administrator OR a rank role at or above AwolKickMinRank.
    /// </summary>
    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;

        var rankList = _config.GetRankRolesList();
        var minRankIdx = rankList.FindIndex(r =>
            r.Equals(_config.AwolKickMinRank, StringComparison.OrdinalIgnoreCase));

        if (minRankIdx < 0)
        {
            _logger.LogWarning(
                "AwolKickMinRank '{Rank}' not found in RankRoles list. " +
                "Permission check will deny everyone except Admins.",
                _config.AwolKickMinRank);
            return false;
        }

        return MemberRankIsAtOrAbove(invoker, rankList, minRankIdx);
    }

    /// <summary>
    /// True if the member holds any rank role at or above the given index in the
    /// rank list. Members with multiple rank roles (which shouldn't happen but
    /// can during a botched promotion) are evaluated by their highest rank.
    /// </summary>
    private static bool MemberRankIsAtOrAbove(
        SocketGuildUser member, List<string> rankList, int minRankIdx)
    {
        foreach (var role in member.Roles)
        {
            var idx = rankList.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= minRankIdx) return true;
        }
        return false;
    }

    /// <summary>
    /// Joins a name list with commas, truncating to keep summary messages
    /// under Discord's 2000-char content cap when many members are involved.
    /// </summary>
    private static string TruncateList(List<string> names, int maxLength = 400)
    {
        var joined = string.Join(", ", names);
        if (joined.Length <= maxLength) return joined;
        return joined.Substring(0, maxLength) + $"… (+{names.Count - 1} more, see audit log)";
    }
}