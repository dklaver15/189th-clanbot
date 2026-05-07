using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /commands slash command — a self-service catalog of every
/// slash command the bot exposes, filtered to only those the caller can
/// actually run at their current rank or permission set.
///
/// ── Why this exists ──
/// Members ask "what can I do" and there was no in-Discord answer; the only
/// reference was the registered command list in DiscordBotService.cs. This
/// gives every member a one-shot, ephemeral view of their available toolkit
/// without leaking commands they can't use.
///
/// ── How permissions are evaluated ──
/// Each catalog entry carries its own predicate that mirrors the permission
/// gate enforced by the actual command's handler. The catalog and the gated
/// handlers can drift if a handler's gate changes — there's a SyncWithHandlers
/// note on each predicate factory and on the two hardcoded permission labels
/// pointing at the authoritative source so future edits are easy to audit.
///
/// Predicate flavors mirror exactly what's in the rest of the codebase:
///   • Everyone                  — no gate
///   • Officer                   — ManageRoles | Administrator | exempt role
///                                 (matches SlashCommandHandler.HasElevatedPermissions
///                                 and CleanupCalendarDupesCommandHandler.HasElevatedPermissions)
///   • OfficerOrManageNicknames  — ManageNicknames | Administrator | exempt role
///                                 (matches SetNickCommandHandler.HasPermission)
///   • MinRank(rank)             — Administrator | role index ≥ minIndex in RankRoles
///                                 (matches every rank-gated handler)
///
/// Administrator always bypasses, in line with every existing handler.
///
/// ── Permission labels ──
/// Rank-gated entries pull their min-rank from BotConfig at render time so
/// renaming a config min-rank in appsettings.json updates the table without
/// a code change. Two handlers (EventCreditCommandHandler, AttendanceCommandHandler)
/// hardcode their floor as a const MinRankFloor — those entries hardcode the
/// matching label here with a SyncWithHandlers note.
///
/// ── Display format ──
/// Renders in a single ephemeral embed with a fenced code-block table.
/// Sorted by permission tier ascending (Everyone → Officer → 2ndLT+ → … → BG+),
/// then alphabetically within tier — easy commands at the top, rare commands
/// at the bottom, in roughly the order a member earns access to them.
/// </summary>
public class CommandsCommandHandler
{
    public const string CommandName = "commands";

    private readonly ILogger<CommandsCommandHandler> _logger;
    private readonly BotConfig _config;

    public CommandsCommandHandler(
        ILogger<CommandsCommandHandler> logger,
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
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleCommandsAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleCommandsAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null || command.User is not SocketGuildUser caller)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var catalog = BuildCatalog(_config);
        var rankRoles = _config.GetRankRolesList();

        var visible = catalog
            .Where(e => e.CanUse(caller, _config))
            .OrderBy(e => e.SortKey(rankRoles))
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

        if (visible.Count == 0)
        {
            // Should not happen — every member can run at least the Everyone-tier
            // commands — but guard anyway so a misconfigured RankRoles list (e.g.
            // emptied after a typo) doesn't dump an empty embed.
            await command.FollowupAsync(
                "No commands are currently available to you. (This is unexpected — let an admin know.)",
                ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("📖 Slash Command Reference")
            .WithDescription(BuildTable(visible))
            .WithColor(Color.Blue)
            .WithFooter($"{visible.Count} command(s) available to you")
            .Build();

        await command.FollowupAsync(embed: embed, ephemeral: true);

        _logger.LogInformation(
            "/commands rendered for {User}: {Count} visible entries",
            caller.Username, visible.Count);
    }

    // ── Rendering ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fixed-width 3-column code-block table. Discord renders code
    /// blocks in monospace so the columns line up. On mobile the block becomes
    /// horizontally scrollable rather than wrapping — preferable to ragged
    /// columns. Total width sized to fit comfortably on desktop while staying
    /// well under the 4096-char embed-description cap (the full 19-row table
    /// comes in around 1.8 KB, plus the surrounding code fences).
    /// </summary>
    private static string BuildTable(List<CommandEntry> entries)
    {
        const int nameWidth = 24;   // longest registered command is /cleanup-calendar-dupes (23 chars w/ slash)
        const int permWidth = 9;    // "Everyone" is 8; rank labels like "2ndLT+" fit in 6
        const int descWidth = 50;

        var sb = new StringBuilder();
        sb.AppendLine("```");
        sb.Append("Command".PadRight(nameWidth)).Append("  ")
          .Append("Min".PadRight(permWidth)).Append("  ")
          .AppendLine("Description");
        sb.Append(new string('─', nameWidth)).Append("  ")
          .Append(new string('─', permWidth)).Append("  ")
          .AppendLine(new string('─', descWidth));

        foreach (var entry in entries)
        {
            sb.Append(Fit($"/{entry.Name}", nameWidth)).Append("  ")
              .Append(Fit(entry.PermissionLabel, permWidth)).Append("  ")
              .AppendLine(Fit(entry.Description, descWidth, truncationEllipsis: true));
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Pad-right to width, truncating with an ellipsis if the value is longer.
    /// truncationEllipsis=false means hard-truncate (used for command names
    /// and permission labels where there's no graceful shortening — but in
    /// practice neither column should ever overflow).
    /// </summary>
    private static string Fit(string value, int width, bool truncationEllipsis = false)
    {
        if (value.Length <= width) return value.PadRight(width);
        if (!truncationEllipsis || width < 2) return value[..width];
        return value[..(width - 1)] + "…";
    }

    // ── Catalog ──────────────────────────────────────────────────────

    /// <summary>One row of the slash-command catalog.</summary>
    /// <param name="Name">Slash command name without the leading slash.</param>
    /// <param name="Description">One-line summary shown in the table.</param>
    /// <param name="PermissionLabel">Display label, e.g. "Everyone", "Officer+", "2ndLT+".</param>
    /// <param name="CanUse">Predicate that mirrors the gate inside the actual command's handler.</param>
    /// <param name="SortKey">Returns a tier int given the live RankRoles list. Lower sorts first.</param>
    private sealed record CommandEntry(
        string Name,
        string Description,
        string PermissionLabel,
        Func<SocketGuildUser, BotConfig, bool> CanUse,
        Func<List<string>, int> SortKey);

    /// <summary>
    /// Build the full catalog of registered slash commands. Order here doesn't
    /// matter — display sort happens at render time. To add a new command,
    /// add an entry below AND register the SlashCommandBuilder in
    /// DiscordBotService.OnReadyAsync — the two lists must stay in sync.
    /// </summary>
    private static List<CommandEntry> BuildCatalog(BotConfig config)
    {
        // ── Predicate factories ──
        Func<SocketGuildUser, BotConfig, bool> everyone = (_, _) => true;

        // SyncWithHandlers: SlashCommandHandler.HasElevatedPermissions,
        //                   CleanupCalendarDupesCommandHandler.HasElevatedPermissions
        Func<SocketGuildUser, BotConfig, bool> officer = (user, cfg) =>
        {
            if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
                return true;
            var exempt = cfg.GetExemptRolesList();
            return user.Roles.Any(r => exempt.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        };

        // SyncWithHandlers: SetNickCommandHandler.HasPermission
        Func<SocketGuildUser, BotConfig, bool> officerOrManageNicknames = (user, cfg) =>
        {
            if (user.GuildPermissions.ManageNicknames || user.GuildPermissions.Administrator)
                return true;
            var exempt = cfg.GetExemptRolesList();
            return user.Roles.Any(r => exempt.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
        };

        // SyncWithHandlers: every rank-gated handler — PromoteCommandHandler.HasPromotePermission,
        //                   DemoteCommandHandler, SetNickCommandHandler (no — that's officer),
        //                   SeedPromotionCreditCommandHandler, CompEventCommandHandler,
        //                   EventCreditCommandHandler.HasMinRankFloor,
        //                   AttendanceCommandHandler.HasMinRankFloor,
        //                   KickAwolsCommandHandler, ClearAwolListCommandHandler,
        //                   BriefingNowCommandHandler. They all use the same RankRoles
        //                   index pattern: Administrator bypass, then any user role at
        //                   index ≥ the min-rank's index.
        static Func<SocketGuildUser, BotConfig, bool> MinRank(string minRank) => (user, cfg) =>
        {
            if (user.GuildPermissions.Administrator) return true;
            var ranks = cfg.GetRankRolesList();
            var minIdx = ranks.FindIndex(r => r.Equals(minRank, StringComparison.OrdinalIgnoreCase));
            if (minIdx < 0) return false;
            return user.Roles.Any(role =>
            {
                var idx = ranks.FindIndex(rr => rr.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
                return idx >= minIdx;
            });
        };

        // ── Sort-key factories ──
        // Tier scale: Everyone = 0, Officer = 1, then ranks pick up RankTierBase + RankRoles index.
        // This puts the Officer band between universal and the lowest rank tier (currently 2ndLT
        // at index 13 in the default RankRoles list, so its sort key is 15).
        const int EveryoneTier = 0;
        const int OfficerTier  = 1;
        const int RankTierBase = 2;

        static Func<List<string>, int> SortEveryone() => _ => EveryoneTier;
        static Func<List<string>, int> SortOfficer()  => _ => OfficerTier;
        static Func<List<string>, int> SortRank(string minRank) => ranks =>
        {
            var idx = ranks.FindIndex(r => r.Equals(minRank, StringComparison.OrdinalIgnoreCase));
            // Unknown rank sinks to the bottom rather than crashing — better than throwing if
            // someone misspells a min-rank in appsettings.json.
            return idx < 0 ? int.MaxValue : RankTierBase + idx;
        };

        // ── Permission labels ──
        // Config-backed labels reflect the live gate so renaming a min-rank in
        // appsettings.json updates the table without a code change. The two
        // hardcoded labels match the const MinRankFloor inside their handlers.
        var promoteDemoteLbl = $"{config.PromoteDemoteMinRank}+";
        var compEventLbl     = $"{config.CompEventMinRank}+";
        var awolKickLbl      = $"{config.AwolKickMinRank}+";
        var briefingLbl      = $"{config.BriefingNowMinRank}+";
        const string eventCreditLbl = "CPT+";   // SyncWithHandlers: EventCreditCommandHandler.MinRankFloor
        const string attendanceLbl  = "MAJ+";   // SyncWithHandlers: AttendanceCommandHandler.MinRankFloor

        // ── Catalog ──
        return new List<CommandEntry>
        {
            // Everyone tier
            new("awol-status", "Show your current activity stats",
                "Everyone", everyone, SortEveryone()),
            new("calendar", "Show upcoming events from the clan calendar",
                "Everyone", everyone, SortEveryone()),
            new("commands", "Show this list of available slash commands",
                "Everyone", everyone, SortEveryone()),
            new("gamertags", "Enter your gamertags (EA, Steam, PSN, Xbox, etc.)",
                "Everyone", everyone, SortEveryone()),
            new("lookup", "Look up someone's gamertags from the roster",
                "Everyone", everyone, SortEveryone()),

            // Officer tier
            new("awol-check", "Check another user's activity stats",
                "Officer+", officer, SortOfficer()),
            new("clear-awol", "Clear a user's AWOL status",
                "Officer+", officer, SortOfficer()),
            new("cleanup-calendar-dupes", "Reconcile calendar/GCal duplicate entries",
                "Officer+", officer, SortOfficer()),
            new("roster-export", "Trigger a roster export to Google Sheets",
                "Officer+", officer, SortOfficer()),
            new("setnick", "Change a member's nickname",
                "Officer+", officerOrManageNicknames, SortOfficer()),

            // Rank-gated tiers — sort by the current value of each min-rank in RankRoles
            new("demote", "Demote a member to a lower rank",
                promoteDemoteLbl, MinRank(config.PromoteDemoteMinRank),
                SortRank(config.PromoteDemoteMinRank)),
            new("promote", "Promote a member to the next rank",
                promoteDemoteLbl, MinRank(config.PromoteDemoteMinRank),
                SortRank(config.PromoteDemoteMinRank)),
            new("seed-promotion-credit", "Apply Seed Events from the roster sheet to DB",
                promoteDemoteLbl, MinRank(config.PromoteDemoteMinRank),
                SortRank(config.PromoteDemoteMinRank)),

            new("comp-event", "Create a competitive division event on the calendar",
                compEventLbl, MinRank(config.CompEventMinRank),
                SortRank(config.CompEventMinRank)),

            new("add-event-credit", "Manually add 1 event credit at member's current rank",
                eventCreditLbl, MinRank("CPT"), SortRank("CPT")),
            new("remove-event-credit", "Remove 1 manual event credit at member's current rank",
                eventCreditLbl, MinRank("CPT"), SortRank("CPT")),

            new("attendance", "Show today's clan event attendance, grouped by event",
                attendanceLbl, MinRank("MAJ"), SortRank("MAJ")),
            new("clear-awol-list", $"Delete all messages in #{config.HqChannelName}",
                awolKickLbl, MinRank(config.AwolKickMinRank),
                SortRank(config.AwolKickMinRank)),
            new("kick-awols", "Kick all members currently flagged AWOL",
                awolKickLbl, MinRank(config.AwolKickMinRank),
                SortRank(config.AwolKickMinRank)),

            new("briefing-now", "Generate the weekly officer briefing immediately",
                briefingLbl, MinRank(config.BriefingNowMinRank),
                SortRank(config.BriefingNowMinRank)),
        };
    }
}
