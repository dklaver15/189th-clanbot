using System.Text;
using System.Text.Json;
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
/// Handles /usage-stats — the read side of the slash-command usage log
/// written by <see cref="CommandUsageTrackingHandler"/>. Three subcommands:
///   • top     — leaderboard of most-used commands in the window
///   • user    — one member's recent invocations
///   • command — drill into a single command (top callers + parameter breakdown)
///
/// ── Permission model ──
/// MAJ+ only (hardcoded floor, same defensive pattern as
/// AttendanceCommandHandler.MinRankFloor — config can't drift below intent).
/// Server Administrators bypass.
///
/// ── Retention boundary ──
/// CommandUsagePruneService deletes rows older than 90 days, so this
/// handler refuses any <c>days</c> value above <see cref="RetentionDays"/>.
/// Asking for 365 days of data would silently return only 90 days of
/// results without that floor — confusing for the caller.
///
/// ── Why subcommand groups ──
/// "top" + "user" + "command" share the same entitlement gate, the same
/// data source, and the same window-clamp logic, so collapsing them under
/// a single /usage-stats keeps the slash-command surface compact (one
/// catalog entry, one rank gate to maintain).
/// </summary>
public class UsageStatsCommandHandler
{
    public const string CommandName = "usage-stats";

    /// <summary>
    /// Hardcoded minimum rank to invoke /usage-stats. Matches the
    /// AttendanceCommandHandler pattern of pinning the floor in code so
    /// BotConfig changes can't accidentally widen access.
    /// </summary>
    private const string MinRankFloor = "MAJ";

    /// <summary>
    /// Retention ceiling enforced by CommandUsagePruneService. Querying past
    /// this is a guaranteed empty range, so we clamp <c>days</c> here and
    /// surface a footer note to the caller.
    /// </summary>
    private const int RetentionDays = 90;
    private const int DefaultDays   = 30;

    // Result-set caps. Discord embed field values cap at 1024 chars and the
    // total embed at 6000; these bounds keep us well under both even on a
    // heavy month of usage.
    private const int TopRows               = 15;
    private const int UserHistoryRows       = 25;
    private const int CommandCallerRows     = 10;
    private const int ParamBreakdownRows    = 10;

    /// <summary>Width budget for the command-name column in the /top table.</summary>
    private const int CommandColumnWidth = 30;

    private readonly IServiceProvider _services;
    private readonly ILogger<UsageStatsCommandHandler> _logger;
    private readonly BotConfig _config;

    public UsageStatsCommandHandler(
        IServiceProvider services,
        ILogger<UsageStatsCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
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
            await DispatchAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", command.Data.Name);
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task DispatchAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasMinRankFloor(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {MinRankFloor}+).",
                ephemeral: true);
            return;
        }

        // Subcommand routing — same shape as InviteCommandHandler. The
        // subcommand option lives at Options[0] with type SubCommand; its
        // own option values live one level deeper at sub.Options.
        var sub = command.Data.Options.FirstOrDefault();
        if (sub is null)
        {
            await command.FollowupAsync(
                "Missing subcommand. Try `/usage-stats top`, `/usage-stats user`, or `/usage-stats command`.",
                ephemeral: true);
            return;
        }

        switch (sub.Name)
        {
            case "top":
                await HandleTopAsync(command, sub);
                break;
            case "user":
                await HandleUserAsync(command, sub);
                break;
            case "command":
                await HandleCommandDetailAsync(command, sub);
                break;
            default:
                await command.FollowupAsync($"Unknown subcommand `{sub.Name}`.", ephemeral: true);
                break;
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // /usage-stats top
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleTopAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        var (days, clampNote) = ResolveDays(sub);
        var cutoff  = DateTime.UtcNow.AddDays(-days);
        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Pull the minimal column set in window, then group in memory.
        // EF Core's SQLite translator has historically struggled with
        // Distinct().Count() inside a GroupBy projection ("could not be
        // translated"), and TopRow's positional-record projection has its
        // own translation edge cases. With 90-day retention plus the
        // 189th's command volume, the in-window row count stays at a few
        // thousand at most — trivially cheap to materialize and group
        // client-side, with the upside that we can use ordinary LINQ
        // without fighting the translator.
        var raw = await db.CommandUsages
            .Where(c => c.GuildId == guildId && c.ExecutedAt >= cutoff)
            .Select(c => new { c.CommandName, c.SubcommandPath, c.UserId })
            .ToListAsync();

        var rows = raw
            .GroupBy(r => new { r.CommandName, r.SubcommandPath })
            .Select(g => new TopRow(
                g.Key.CommandName,
                g.Key.SubcommandPath,
                g.Count(),
                g.Select(x => x.UserId).Distinct().Count()))
            .OrderByDescending(r => r.Uses)
            .Take(TopRows)
            .ToList();

        if (rows.Count == 0)
        {
            await command.FollowupAsync(
                $"No slash-command usage in the last {days} day(s).",
                ephemeral: true);
            return;
        }

        var table = new StringBuilder();
        table.AppendLine("```");
        table.AppendLine(
            "#  ".PadRight(4) +
            "Command".PadRight(CommandColumnWidth) + " " +
            "Uses".PadLeft(6) + " " +
            "Users".PadLeft(6));
        table.AppendLine(new string('─', 4 + CommandColumnWidth + 1 + 6 + 1 + 6));

        var rank = 1;
        foreach (var r in rows)
        {
            var name = FormatCommandName(r.Command, r.Subcommand);
            if (name.Length > CommandColumnWidth)
                name = name.Substring(0, CommandColumnWidth - 3) + "...";

            table.AppendLine(
                rank.ToString().PadRight(4) +
                name.PadRight(CommandColumnWidth) + " " +
                r.Uses.ToString().PadLeft(6) + " " +
                r.UniqueUsers.ToString().PadLeft(6));
            rank++;
        }
        table.AppendLine("```");

        var embed = new EmbedBuilder()
            .WithTitle($"📊 Top slash-command usage — last {days} day(s)")
            .WithDescription(table.ToString())
            .WithColor(Color.Blue);
        if (!string.IsNullOrEmpty(clampNote))
            embed.WithFooter(clampNote);

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ───────────────────────────────────────────────────────────────────
    // /usage-stats user
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleUserAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        // SocketUser (not SocketGuildUser) so we can still query history for
        // members who have since left the guild. The picker resolves at
        // invocation time, but the underlying record may outlive their
        // membership.
        var memberOpt = sub.Options.FirstOrDefault(o => o.Name == "member");
        if (memberOpt?.Value is not SocketUser target)
        {
            await command.FollowupAsync("Could not resolve the `member` option.", ephemeral: true);
            return;
        }

        var (days, clampNote) = ResolveDays(sub);
        var cutoff  = DateTime.UtcNow.AddDays(-days);
        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var totalCount = await db.CommandUsages.CountAsync(c =>
            c.GuildId == guildId &&
            c.UserId  == target.Id &&
            c.ExecutedAt >= cutoff);

        var recent = await db.CommandUsages
            .Where(c => c.GuildId == guildId
                     && c.UserId  == target.Id
                     && c.ExecutedAt >= cutoff)
            .OrderByDescending(c => c.ExecutedAt)
            .Take(UserHistoryRows)
            .ToListAsync();

        var embed = new EmbedBuilder()
            .WithTitle($"📋 {target.Username} — slash-command history")
            .WithColor(Color.Blue)
            .WithDescription($"**{totalCount}** invocation(s) in the last {days} day(s).");

        if (recent.Count == 0)
        {
            embed.AddField("Recent activity", "_No usage in this window._");
        }
        else
        {
            var lines = new StringBuilder();
            foreach (var r in recent)
            {
                // <t:UNIX:R> renders as "2 hours ago" client-side, which
                // ages gracefully without us having to compute it server-side.
                var ts = new DateTimeOffset(r.ExecutedAt, TimeSpan.Zero).ToUnixTimeSeconds();
                var name = FormatCommandName(r.CommandName, r.SubcommandPath);
                var paramSuffix = FormatParametersInline(r.Parameters);
                lines.AppendLine($"• `/{name}`{paramSuffix} — <t:{ts}:R>");
            }

            // Defensive: trim if we somehow exceed Discord's 1024-char field cap.
            var fieldValue = lines.ToString();
            if (fieldValue.Length > 1024) fieldValue = fieldValue.Substring(0, 1020) + "…";

            embed.AddField($"Last {recent.Count} invocation(s)", fieldValue);
            if (totalCount > recent.Count)
                embed.WithFooter($"Showing {recent.Count} of {totalCount}." +
                                 (string.IsNullOrEmpty(clampNote) ? "" : " " + clampNote));
            else if (!string.IsNullOrEmpty(clampNote))
                embed.WithFooter(clampNote);
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ───────────────────────────────────────────────────────────────────
    // /usage-stats command
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleCommandDetailAsync(SocketSlashCommand command, SocketSlashCommandDataOption sub)
    {
        var nameOpt = sub.Options.FirstOrDefault(o => o.Name == "name");
        var rawName = nameOpt?.Value as string;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            await command.FollowupAsync("Pass a command name in the `name` option (e.g. `command-catalog`).", ephemeral: true);
            return;
        }

        // Strip a leading slash if a user types "/command-catalog" — we
        // store names without it. Lowercase to match the case-insensitive
        // way Discord normalizes command names.
        var queryName = rawName.Trim().TrimStart('/').ToLowerInvariant();

        var (days, clampNote) = ResolveDays(sub);
        var cutoff  = DateTime.UtcNow.AddDays(-days);
        var guildId = command.GuildId!.Value;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var matchingRows = await db.CommandUsages
            .Where(c => c.GuildId == guildId
                     && c.CommandName == queryName
                     && c.ExecutedAt >= cutoff)
            .ToListAsync();

        if (matchingRows.Count == 0)
        {
            await command.FollowupAsync(
                $"No usage data for `/{queryName}` in the last {days} day(s).",
                ephemeral: true);
            return;
        }

        // Top callers — group in memory; the matchingRows set is bounded by
        // 90-day retention × per-command volume, which for this command
        // surface won't exceed a few thousand rows even at peak. EF
        // GroupBy + Select would also work; in-memory keeps the parameter
        // breakdown below in a single pass.
        var topCallers = matchingRows
            .GroupBy(r => new { r.UserId, r.Username })
            .Select(g => new { g.Key.UserId, g.Key.Username, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(CommandCallerRows)
            .ToList();

        var uniqueUsers = matchingRows.Select(r => r.UserId).Distinct().Count();

        var embed = new EmbedBuilder()
            .WithTitle($"📈 /{queryName} — last {days} day(s)")
            .WithColor(Color.Blue)
            .WithDescription(
                $"**{matchingRows.Count}** invocation(s) by **{uniqueUsers}** unique user(s).");

        // Top callers field
        var callersText = new StringBuilder();
        var rank = 1;
        foreach (var c in topCallers)
        {
            callersText.AppendLine($"{rank}. <@{c.UserId}> — {c.Count}");
            rank++;
        }
        embed.AddField($"Top callers (top {Math.Min(CommandCallerRows, topCallers.Count)})", callersText.ToString());

        // Parameter-value breakdown — only meaningful when this command has
        // allowlisted params. Group by raw JSON string; in practice the
        // serializer + Discord builder option order make this stable per
        // (command, param-set), so equal inputs collapse into one row.
        var paramRows = matchingRows
            .Where(r => !string.IsNullOrEmpty(r.Parameters))
            .GroupBy(r => r.Parameters!)
            .Select(g => new { Params = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(ParamBreakdownRows)
            .ToList();

        if (paramRows.Count > 0)
        {
            var paramText = new StringBuilder();
            foreach (var p in paramRows)
            {
                var pretty = FormatParametersBlock(p.Params);
                paramText.AppendLine($"• {pretty} — **{p.Count}**");
            }
            var fieldValue = paramText.ToString();
            if (fieldValue.Length > 1024) fieldValue = fieldValue.Substring(0, 1020) + "…";
            embed.AddField("Parameter breakdown", fieldValue);
        }
        else
        {
            embed.AddField("Parameter breakdown",
                "_This command has no allowlisted parameters, " +
                "or none were used in the window. " +
                "See CommandUsageTrackingHandler.ParameterAllowlist._");
        }

        if (!string.IsNullOrEmpty(clampNote))
            embed.WithFooter(clampNote);

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ───────────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the optional <c>days</c> sub-option, defaults to 30, and clamps
    /// to [1, RetentionDays]. Returns a human-readable note when clamping
    /// happened so we can surface it as an embed footer.
    /// </summary>
    private static (int days, string? clampNote) ResolveDays(SocketSlashCommandDataOption sub)
    {
        var raw = sub.Options.FirstOrDefault(o => o.Name == "days")?.Value as long?;
        if (raw is null) return (DefaultDays, null);

        var days = (int)raw.Value;
        if (days < 1)
            return (1, "`days` was below 1; using 1.");
        if (days > RetentionDays)
            return (RetentionDays, $"`days` was above {RetentionDays} (retention limit); clamped.");
        return (days, null);
    }

    /// <summary>
    /// Joins the top-level command name and optional subcommand path into a
    /// single display string ("invite create", "command-catalog"). Used by
    /// both the /top table and the /user history list.
    /// </summary>
    private static string FormatCommandName(string command, string? subcommand) =>
        string.IsNullOrEmpty(subcommand) ? command : $"{command} {subcommand}";

    /// <summary>
    /// Renders an inline ` (style:list)` suffix for a /user history line.
    /// Returns empty string for null or empty Parameters JSON. Best-effort —
    /// if JSON parse fails we return empty rather than dumping malformed
    /// state into the embed.
    /// </summary>
    private static string FormatParametersInline(string? parametersJson)
    {
        if (string.IsNullOrEmpty(parametersJson)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;

            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                parts.Add($"{prop.Name}:{StringifyJsonValue(prop.Value)}");

            return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Renders a Parameters JSON blob for the /command parameter-breakdown
    /// list as a backticked key:value pair sequence. Falls back to the raw
    /// JSON if parsing fails.
    /// </summary>
    private static string FormatParametersBlock(string parametersJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return $"`{parametersJson}`";

            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                parts.Add($"`{prop.Name}:{StringifyJsonValue(prop.Value)}`");
            return parts.Count == 0 ? "_(no params)_" : string.Join(" ", parts);
        }
        catch (JsonException)
        {
            return $"`{parametersJson}`";
        }
    }

    private static string StringifyJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String  => value.GetString() ?? string.Empty,
        JsonValueKind.Number  => value.GetRawText(),
        JsonValueKind.True    => "true",
        JsonValueKind.False   => "false",
        JsonValueKind.Null    => "null",
        _                     => value.GetRawText(),
    };

    /// <summary>
    /// MAJ+ rank gate. Same role-list-index pattern as
    /// AttendanceCommandHandler.HasMinRankFloor. Server Administrators
    /// bypass the rank check entirely.
    /// </summary>
    private bool HasMinRankFloor(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex = rankRoles.FindIndex(r =>
            r.Equals(MinRankFloor, StringComparison.OrdinalIgnoreCase));
        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r =>
                r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    private record TopRow(string Command, string? Subcommand, int Uses, int UniqueUsers);

    /// <summary>
    /// Slash-command shape for the OnReadyAsync registration list. Lives on
    /// the handler so the option list stays next to the code that consumes
    /// it — same pattern as InviteCommandHandler.BuildCommand() and
    /// CleanupCalendarDupesCommandHandler.BuildCommand(). Catalog entry in
    /// CommandsCommandHandler.BuildCatalog must stay in sync.
    /// </summary>
    public static SlashCommandProperties BuildCommand()
    {
        return new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Read the slash-command usage log (MAJ+ only)")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("top")
                .WithDescription("Most-used commands in the window")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("days", ApplicationCommandOptionType.Integer,
                    $"Window size in days (1–{RetentionDays}, default {DefaultDays})",
                    isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("user")
                .WithDescription("One member's recent invocations")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("member", ApplicationCommandOptionType.User,
                    "The member whose history to show", isRequired: true)
                .AddOption("days", ApplicationCommandOptionType.Integer,
                    $"Window size in days (1–{RetentionDays}, default {DefaultDays})",
                    isRequired: false))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("command")
                .WithDescription("Drill into a single command — top callers and parameter breakdown")
                .WithType(ApplicationCommandOptionType.SubCommand)
                .AddOption("name", ApplicationCommandOptionType.String,
                    "Command name (e.g. command-catalog, calendar, invite)",
                    isRequired: true)
                .AddOption("days", ApplicationCommandOptionType.Integer,
                    $"Window size in days (1–{RetentionDays}, default {DefaultDays})",
                    isRequired: false))
            .Build();
    }
}