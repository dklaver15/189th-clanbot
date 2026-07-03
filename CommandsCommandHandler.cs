using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /command-catalog slash command — a self-service catalog of every
/// slash command the bot exposes, filtered to only those the caller can
/// actually run at their current rank or permission set.
///
/// ── Why this exists ──
/// Members ask "what can I do" and there was no in-Discord answer; the only
/// reference was the registered command list in DiscordBotService.cs. This
/// gives every member a one-shot, ephemeral view of their available toolkit
/// without leaking commands they can't use.
///
/// ── Pagination ──
/// The catalog renders in pages of PageSize entries with Previous / Page X/Y /
/// Next buttons. Buttons are namespaced with the ButtonIdPrefix custom-id so
/// other future button handlers don't collide. Each click re-runs the catalog
/// filter against the caller's current roles — state is fully derived from
/// (caller + target page), nothing is cached, so role changes mid-session are
/// reflected on the next click. component.UpdateAsync edits the original
/// ephemeral message in place rather than posting a new one.
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
/// Renders in a single ephemeral embed, with a style chosen by the caller via
/// the optional `style` slash-command option (default Table):
///   • Table — fenced code block with column verticals + ANSI bold (desktop-friendly).
///   • List  — markdown-formatted entries, one command per two-line block
///             (mobile-friendly; no monospace assumption).
/// Both styles use the same alphabetical sort and the same pagination scheme.
/// </summary>
public class CommandsCommandHandler
{
    public const string CommandName = "command-catalog";

    /// <summary>
    /// Custom-id prefix for every component button this handler attaches.
    /// Other button handlers (current or future) must use a different prefix
    /// so HandleButtonAsync only acts on its own clicks.
    /// </summary>
    private const string ButtonIdPrefix = "cmdcat:";

    /// <summary>
    /// How many catalog rows fit on one page. With 19 registered commands
    /// (as of writing) this gives 3 pages (8 + 8 + 3) — a comfortable
    /// scan length per page without making pagination feel like overkill.
    /// </summary>
    private const int PageSize = 8;

    /// <summary>Slash-command option name for picking the render style.</summary>
    private const string StyleOptionName = "style";

    /// <summary>
    /// Render styles offered through the `style` slash-command option.
    /// Encoded as a single character in pagination button custom-ids
    /// (see ParseGotoTarget / BuildPage) to keep them compact.
    /// </summary>
    private enum RenderStyle
    {
        Table,  // code block with verticals + ANSI bold (desktop-friendly)
        List    // markdown two-line blocks (mobile-friendly)
    }

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
        client.ButtonExecuted       += HandleButtonAsync;
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

        var visible = BuildVisibleList(caller);

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

        var style = ReadStyleOption(command);
        var (embed, components) = BuildPage(visible, page: 0, style);
        await command.FollowupAsync(embed: embed, components: components, ephemeral: true);

        _logger.LogInformation(
            "/{Command} rendered for {User} ({Style}): {Count} visible entries across {Pages} page(s)",
            CommandName, caller.Username, style, visible.Count, PageCount(visible.Count));
    }

    /// <summary>
    /// Read the style option from the slash-command invocation. Returns
    /// RenderStyle.Table when the option is absent or unrecognised — Table
    /// is the original (pre-option) behaviour, so missing data falls back to
    /// what existing users expect.
    /// </summary>
    private static RenderStyle ReadStyleOption(SocketSlashCommand command)
    {
        var raw = command.Data.Options
            .FirstOrDefault(o => o.Name == StyleOptionName)?
            .Value as string;
        return ParseStyle(raw);
    }

    private static RenderStyle ParseStyle(string? raw) => raw switch
    {
        "list"  => RenderStyle.List,
        "table" => RenderStyle.Table,
        _       => RenderStyle.Table,   // null / unknown → default
    };

    /// <summary>Single-character code used in pagination button custom-ids.</summary>
    private static char EncodeStyle(RenderStyle style) => style switch
    {
        RenderStyle.List => 'l',
        _                => 't',
    };

    private static RenderStyle DecodeStyle(string code) => code switch
    {
        "l" => RenderStyle.List,
        _   => RenderStyle.Table,
    };

    /// <summary>
    /// Component-button handler for pagination. Filters by ButtonIdPrefix so
    /// it doesn't react to other handlers' buttons. State is fully derived
    /// from (caller + target page) — nothing cached, so roles changing
    /// mid-session are reflected on the next click. UpdateAsync edits the
    /// original ephemeral message in place.
    /// </summary>
    private async Task HandleButtonAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(ButtonIdPrefix, StringComparison.Ordinal))
            return;

        try
        {
            var target = ParseGotoTarget(component.Data.CustomId);
            if (target is null)
            {
                // Unknown action under our prefix (e.g. the disabled "noop"
                // page-indicator button that Discord shouldn't even fire for,
                // but in case a client does, ack it without changing state).
                await component.DeferAsync();
                return;
            }

            if (component.User is not SocketGuildUser caller)
            {
                await component.DeferAsync();
                return;
            }

            var (targetPage, style) = target.Value;

            var visible = BuildVisibleList(caller);
            var (embed, components) = BuildPage(visible, targetPage, style);

            await component.UpdateAsync(msg =>
            {
                msg.Embed = embed;
                msg.Components = components;
            });

            _logger.LogDebug(
                "/{Command} pagination: {User} -> page {Page} ({Style})",
                CommandName, caller.Username, targetPage + 1, style);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command} pagination button", CommandName);
            try
            {
                // RespondAsync rather than FollowupAsync because the original
                // interaction may not have been deferred yet at the failure point.
                await component.RespondAsync(
                    "Something went wrong navigating pages. Try running /command-catalog again.",
                    ephemeral: true);
            }
            catch { /* already responded or interaction expired */ }
        }
    }

    /// <summary>
    /// Parse a "cmdcat:goto:N:S" custom-id into (page, style). The style
    /// segment (S) is a single char: 't' = Table, 'l' = List. Returns null
    /// for any other shape (including the disabled "noop" button and any
    /// pre-style-option custom-ids that might still be sitting in old
    /// unrefreshed embeds — those will gracefully fall through and the user
    /// can re-run /command-catalog to get a fresh embed).
    /// </summary>
    private static (int Page, RenderStyle Style)? ParseGotoTarget(string customId)
    {
        var parts = customId.Split(':');
        if (parts.Length < 4) return null;
        if (parts[1] != "goto") return null;
        if (!int.TryParse(parts[2], out var page)) return null;
        return (page, DecodeStyle(parts[3]));
    }

    /// <summary>
    /// Build the rank-filtered, sorted list of catalog entries the caller can
    /// currently use. Pulled out so both the slash-command entry point and the
    /// button-update path share one code path.
    /// </summary>
    private List<CommandEntry> BuildVisibleList(SocketGuildUser caller)
    {
        return BuildCatalog(_config)
            .Where(e => e.CanUse(caller, _config))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Render one page of the visible-commands list, returning the embed and
    /// (when pagination applies) the button row. Body rendering is dispatched
    /// to BuildTableBody / BuildListBody by RenderStyle. When the entire list
    /// fits on a single page the components are null and Discord won't show
    /// buttons. The chosen style is encoded into the button custom-ids so
    /// clicking Next/Previous keeps the user in their picked style.
    /// </summary>
    private static (Embed Embed, MessageComponent? Components) BuildPage(
        List<CommandEntry> all, int page, RenderStyle style)
    {
        var totalPages = PageCount(all.Count);
        var safePage = Math.Clamp(page, 0, totalPages - 1);
        var pageItems = all.Skip(safePage * PageSize).Take(PageSize).ToList();

        var body = style switch
        {
            RenderStyle.List => BuildListBody(pageItems),
            _                => BuildTableBody(pageItems),
        };

        var embed = new EmbedBuilder()
            .WithTitle("📖 Slash Command Reference")
            .WithDescription(body)
            .WithColor(Color.Blue)
            .WithFooter(
                $"Page {safePage + 1} of {totalPages} • {all.Count} command(s) available to you")
            .Build();

        if (totalPages <= 1) return (embed, null);

        // Three-button row: Previous, page indicator (disabled, acts as label),
        // Next. Discord auto-disables click handling on disabled buttons but we
        // still namespace its custom-id under our prefix so HandleButtonAsync
        // can early-return cleanly if any client ever does fire it.
        var styleCode = EncodeStyle(style);
        var components = new ComponentBuilder()
            .WithButton(
                label: "« Previous",
                customId: $"{ButtonIdPrefix}goto:{safePage - 1}:{styleCode}",
                style: ButtonStyle.Secondary,
                disabled: safePage == 0)
            .WithButton(
                label: $"Page {safePage + 1} / {totalPages}",
                customId: $"{ButtonIdPrefix}noop",
                style: ButtonStyle.Secondary,
                disabled: true)
            .WithButton(
                label: "Next »",
                customId: $"{ButtonIdPrefix}goto:{safePage + 1}:{styleCode}",
                style: ButtonStyle.Secondary,
                disabled: safePage >= totalPages - 1)
            .Build();

        return (embed, components);
    }

    private static int PageCount(int total) =>
        Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));

    // ── Rendering ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a bordered code-block table. Discord renders code blocks in
    /// monospace so columns + verticals line up. Row layout per entry:
    ///
    ///   Command                   │ Min       │ Description
    ///                             │           │
    ///   /awol-status              │ Everyone  │ Show your current activity stats
    ///                             │           │
    ///   /calendar                 │ Everyone  │ Show upcoming events…
    ///
    /// The blank row between entries keeps the column verticals (│) continuous
    /// rather than breaking into per-row mini-tables, while still giving each
    /// entry visible breathing room. On mobile the block becomes horizontally
    /// scrollable rather than wrapping — preferable to ragged columns.
    ///
    /// ── Bolding via ANSI ──
    /// We use a ```ansi``` fence (instead of plain ```) so Discord's ANSI
    /// renderer picks up \u001b[1m...\u001b[0m as bold. This applies to the
    /// column headers and to each command name in the body. ANSI rendering is
    /// fully supported on Discord desktop and the browser client; mobile
    /// support has historically been patchy and may show the raw escape
    /// sequences instead of bold text. If that turns out to be a problem for
    /// the clan, revert by changing "ansi" back to no language tag and
    /// swapping BoldFit calls back to Fit (3 call sites).
    ///
    /// Total row width with current column sizes is ~104 visible chars, well
    /// under the 4096-char embed-description cap with PageSize=8 entries per
    /// page. The ANSI control bytes don't take visible width and add ~16
    /// chars per row to the raw string length — still comfortably under cap.
    /// </summary>
    private static string BuildTableBody(List<CommandEntry> entries)
    {
        const int nameWidth = 24;   // longest registered command is /cleanup-calendar-dupes (23 chars w/ slash)
        const int permWidth = 9;    // "Everyone" is 8; rank labels like "2ndLT+" fit in 6
        const int descWidth = 65;   // every current description fits unwrapped

        // Three column-segments separated by " │ " on content rows and
        // matching "─┼─" on the header rule. Each Pad+1 width on either side
        // of the separator gives the verticals visual breathing room without
        // wasting much horizontal space.
        string headerRule =
            new string('─', nameWidth + 2) + "┼" +
            new string('─', permWidth + 2) + "┼" +
            new string('─', descWidth + 2);

        // Continuation row that preserves the column verticals through the
        // breathing-room gap between entries. Only the first two columns get
        // explicit trailing spaces — the third column's trailing whitespace
        // would just bloat the description without adding visual structure.
        string blankRow =
            new string(' ', nameWidth + 2) + "│" +
            new string(' ', permWidth + 2) + "│";

        var sb = new StringBuilder();
        sb.AppendLine("```ansi");

        // Header — bold the column titles for visual hierarchy.
        sb.Append(' ').Append(BoldFit("Command", nameWidth)).Append(" │ ")
          .Append(BoldFit("Min", permWidth)).Append(" │ ")
          .Append(Bold("Description")).AppendLine();
        sb.AppendLine(headerRule);

        // Rows, with a blank-with-verticals row inserted between consecutive entries.
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            sb.Append(' ').Append(BoldFit($"/{entry.Name}", nameWidth)).Append(" │ ")
              .Append(Fit(entry.PermissionLabel, permWidth)).Append(" │ ")
              .AppendLine(Fit(entry.Description, descWidth, truncationEllipsis: true));

            if (i < entries.Count - 1)
                sb.AppendLine(blankRow);
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a markdown-formatted list body for mobile-friendly rendering.
    /// Each entry occupies two content lines plus a blank-line separator:
    ///
    ///   **`/awol-status`** — Everyone
    ///   Your activity stats
    ///
    ///   **`/calendar`** — Everyone
    ///   Upcoming clan calendar events
    ///
    /// No code block — Discord's native markdown renderer handles bold and
    /// inline-code styling, which works identically on desktop, web, and
    /// mobile clients. The em dash (—) separates the command name from its
    /// permission label without needing column alignment, sidestepping the
    /// monospace assumptions that make the Table style wrap awkwardly on
    /// narrow screens. Permission labels stay unstyled to keep the visual
    /// emphasis on the command name itself.
    ///
    /// Total page size with PageSize=8 entries lands around ~1 KB — far
    /// below the 4096-char embed-description cap.
    /// </summary>
    private static string BuildListBody(List<CommandEntry> entries)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            sb.Append("**`/").Append(entry.Name).Append("`** — ").AppendLine(entry.PermissionLabel);
            sb.AppendLine(entry.Description);
            if (i < entries.Count - 1)
                sb.AppendLine();
        }
        return sb.ToString();
    }

    // ANSI escape sequences understood by Discord's renderer in ```ansi blocks.
    // The escape character itself is U+001B (ESC).
    private const string AnsiBold  = "\u001b[1m";
    private const string AnsiReset = "\u001b[0m";

    /// <summary>Wrap value in ANSI bold without padding.</summary>
    private static string Bold(string value) => $"{AnsiBold}{value}{AnsiReset}";

    /// <summary>
    /// Bold-wrap then pad to the given width. Padding is applied OUTSIDE the
    /// ANSI markers so trailing column whitespace stays plain — keeps the raw
    /// string short and avoids any oddness if a client ever decides to color
    /// padding spaces.
    /// </summary>
    private static string BoldFit(string value, int width)
    {
        if (value.Length > width) value = value[..width];
        var padding = new string(' ', width - value.Length);
        return $"{AnsiBold}{value}{AnsiReset}{padding}";
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
    private sealed record CommandEntry(
        string Name,
        string Description,
        string PermissionLabel,
        Func<SocketGuildUser, BotConfig, bool> CanUse);

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
        //                   CleanupCalendarDupesCommandHandler.HasElevatedPermissions,
        //                   SquadCommandHandler.HasElevatedPermissions
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

        // ── Permission labels ──
        // Config-backed labels reflect the live gate so renaming a min-rank in
        // appsettings.json updates the table without a code change. The two
        // hardcoded labels match the const MinRankFloor inside their handlers.
        var promoteDemoteLbl = $"{config.PromoteDemoteMinRank}+";
        var compEventLbl     = $"{config.CompEventMinRank}+";
        var awolKickLbl      = $"{config.AwolKickMinRank}+";
        var briefingLbl      = $"{config.BriefingNowMinRank}+";
        const string eventCreditLbl   = "CPT+";   // SyncWithHandlers: EventCreditCommandHandler.MinRankFloor
        const string attendanceLbl    = "MAJ+";   // SyncWithHandlers: AttendanceCommandHandler.MinRankFloor
        const string securityAuditLbl = "BG+";    // SyncWithHandlers: SecurityAuditCommandHandler.MinRankFloor

        // ── Catalog ──
        // Display order is alphabetical by command name (sorted at render
        // time in BuildVisibleList), so order here is purely organizational.
        // Grouped by permission tier for human readability when scanning the
        // source — has no effect on the rendered table.
        return new List<CommandEntry>
        {
            // Everyone tier
            new("awol-status", "Show your current activity stats",
                "Everyone", everyone),
            new("calendar", "Show upcoming events from the clan calendar",
                "Everyone", everyone),
            new("command-catalog", "Show this list of available slash commands",
                "Everyone", everyone),
            new("finals-rank", "Look up a player's rank on THE FINALS leaderboard",
                "Everyone", everyone),
            new("finals-club", "Show a club's ranked players on THE FINALS leaderboard",
                "Everyone", everyone),
            new("lookup", "Look up someone's gamertags from the roster",
                "Everyone", everyone),
            new("my-invites", "Show invites you created with use counts and attribution",
                "Everyone", everyone),
            new("patrol", "Toggle your visibility on Patrol Watch embeds (off/on/info)",
                "Everyone", everyone),
            new("promo-eligibility", "Check a member's auto-promotion eligibility",
                "Everyone", everyone),

            // Officer tier
            new("awol-check", "Check another user's activity stats",
                "Officer+", officer),
            new("late-check", "How often a member showed up late to their own events",
                attendanceLbl, MinRank("MAJ")),   // SyncWithHandlers: LateCheckCommandHandler.MinRankFloor
            new("banhammer", "Repost the Ban Hammer counter embed if it gets deleted",
                "Officer+", officer),   // SyncWithHandlers: BanHammerHandler.HasElevatedPermissions
            new("clear-awol", "Clear a user's AWOL status",
                "Officer+", officer),
            new("cleanup-calendar-dupes", "Reconcile calendar/GCal duplicate entries",
                "Officer+", officer),
            new("health", "Bot health diagnostic — uptime, DB stats, scheduled jobs, queues",
                "Officer+", officer),   // SyncWithHandlers: HealthCommandHandler.HasElevatedPermissions
            new("roster-export", "Trigger a roster export to Google Sheets",
                "Officer+", officer),
            new("setnick", "Change a member's nickname",
                "Officer+", officerOrManageNicknames),
            new("squads", "Randomize everyone in the events VC into squads (default 4 per squad, or set size:)",
                "Officer+", officer),
            new("timeline", "Show a member's complete history — joins, ranks, AWOL, applications, events",
                "Officer+", officer),   // SyncWithHandlers: TimelineCommandHandler.HasElevatedPermissions

            // Rank-gated tiers
            new("demote", "Demote a member to a lower rank",
                promoteDemoteLbl, MinRank(config.PromoteDemoteMinRank)),
            new("promote", "Promote a member to the next rank",
                promoteDemoteLbl, MinRank(config.PromoteDemoteMinRank)),
            new("seed-promotion-credit", "Apply Seed Events from the roster sheet to DB",
                promoteDemoteLbl, MinRank(config.PromoteDemoteMinRank)),

            new("comp-event", "Create a competitive division event on the calendar",
                compEventLbl, MinRank(config.CompEventMinRank)),

            new("qotd", "Post a Question of the Day — set it up in DMs",
                $"{config.QotdMinRank}+", MinRank(config.QotdMinRank)),   // SyncWithHandlers: QotdCommandHandler.HasPermission

            new("add-event-credit", "Manually add 1 event credit at member's current rank",
                eventCreditLbl, MinRank("CPT")),
            new("remove-event-credit", "Remove 1 manual event credit at member's current rank",
                eventCreditLbl, MinRank("CPT")),

            new("attendance", "Show today's clan event attendance, grouped by event",
                attendanceLbl, MinRank("MAJ")),
            new("leads", "Reddit recruitment lead stats and recent feed",
                attendanceLbl, MinRank("MAJ")),
            new("usage-stats", "Slash-command usage log (leaderboard, per-member history, drill-down)",
                attendanceLbl, MinRank("MAJ")),   // SyncWithHandlers: UsageStatsCommandHandler.MinRankFloor
            new("security-audit", "Server-protection audit log (account-age gate and future security features)",
                securityAuditLbl, MinRank("BG")),   // SyncWithHandlers: SecurityAuditCommandHandler.MinRankFloor
            new("webhook-audit", "List every webhook in the server, grouped by channel (BG+ only)",
                securityAuditLbl, MinRank("BG")),   // SyncWithHandlers: WebhookAuditCommandHandler.MinRankFloor
            new("clear-awol-list", $"Delete all messages in #{config.HqChannelName}",
                awolKickLbl, MinRank(config.AwolKickMinRank)),
            new("kick-awols", "Kick all members currently flagged AWOL",
                awolKickLbl, MinRank(config.AwolKickMinRank)),

            new("briefing-now", "Generate the weekly officer briefing immediately",
                briefingLbl, MinRank(config.BriefingNowMinRank)),
        };
    }
}