using System.Text.Json;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Logs every slash-command invocation to the CommandUsages table. Captures
/// invoker, guild, channel, command name, full subcommand path, and — for
/// an explicit allowlist of parameters — the values the user passed.
///
/// ── Why an allowlist for parameter values ──
/// Most slash commands take user pickers, gamertag strings, rank names, or
/// nicknames that we do NOT want sitting in an audit log. The handful of
/// commands with bool / choice flags (/command-catalog style, /calendar
/// view, /kick-awols dry-run, etc.) ARE useful to log because we want to
/// know which mode members are picking. Add a (command, param) entry to
/// <see cref="ParameterAllowlist"/> when a new flag-style param is worth
/// surfacing in usage analytics; everything else has values dropped at
/// write time. Subcommand path is logged unconditionally — it's structural,
/// not user content.
///
/// ── Not an activity signal ──
/// Command usage is purely descriptive ("are members using our tooling?").
/// It is intentionally NOT consumed by AutoPromotionService — we don't want
/// to reward members for spamming /command-catalog.
///
/// ── Retention ──
/// CommandUsagePruneService deletes rows older than 90 days, sweeping once
/// at startup and once per 24h thereafter.
///
/// ── Failure mode ──
/// Tracking must never break command execution. Any exception in the write
/// path is logged at Warning and swallowed. The other handlers' work is
/// independent of this one's success.
/// </summary>
public class CommandUsageTrackingHandler
{
    /// <summary>
    /// Map of top-level command name → set of leaf option names whose VALUES
    /// we log. Subcommand path is always captured separately and is not
    /// governed by this list. Names are matched case-insensitively to match
    /// Discord's slash-command name normalization.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> ParameterAllowlist =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["command-catalog"]        = new(StringComparer.OrdinalIgnoreCase) { "style" },
            ["calendar"]               = new(StringComparer.OrdinalIgnoreCase) { "days", "view" },
            ["kick-awols"]             = new(StringComparer.OrdinalIgnoreCase) { "confirm", "dry-run" },
            ["clear-awol-list"]        = new(StringComparer.OrdinalIgnoreCase) { "confirm" },
            ["cleanup-calendar-dupes"] = new(StringComparer.OrdinalIgnoreCase) { "dry_run" },
        };

    private readonly IServiceProvider _services;
    private readonly ILogger<CommandUsageTrackingHandler> _logger;

    public CommandUsageTrackingHandler(
        IServiceProvider services,
        ILogger<CommandUsageTrackingHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleAsync;
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        try
        {
            var (subcommandPath, leafOptions) = WalkOptions(command.Data.Options);
            var parametersJson = BuildParametersJson(command.Data.Name, leafOptions);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            db.CommandUsages.Add(new CommandUsage
            {
                GuildId        = command.GuildId ?? 0,
                UserId         = command.User.Id,
                Username       = command.User.Username,
                ChannelId      = command.ChannelId ?? 0,
                CommandName    = command.Data.Name,
                SubcommandPath = string.IsNullOrEmpty(subcommandPath) ? null : subcommandPath,
                Parameters     = parametersJson,
                ExecutedAt     = DateTime.UtcNow,
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record usage for /{Command}", command.Data.Name);
        }
    }

    /// <summary>
    /// Walks the option tree to extract (a) the space-separated subcommand
    /// path and (b) the flat list of leaf options the user actually passed.
    ///
    /// Discord nests subcommand groups → subcommands → leaf options inside
    /// each layer's <c>Options</c> collection, with the group/subcommand
    /// names appearing as option NAMES at type SubCommandGroup / SubCommand.
    /// So /invite create label:foo arrives as:
    ///   Options[0] { Name="create", Type=SubCommand,
    ///                Options=[{Name="label", Value="foo"}] }
    /// and the path we want is "create" with leaf [{label, foo}].
    /// </summary>
    private static (string Path, IReadOnlyList<SocketSlashCommandDataOption> LeafOptions)
        WalkOptions(IReadOnlyCollection<SocketSlashCommandDataOption>? options)
    {
        var pathParts = new List<string>();
        IEnumerable<SocketSlashCommandDataOption> current =
            options ?? Array.Empty<SocketSlashCommandDataOption>();

        while (true)
        {
            var nesting = current.FirstOrDefault(o =>
                o.Type == ApplicationCommandOptionType.SubCommand ||
                o.Type == ApplicationCommandOptionType.SubCommandGroup);

            if (nesting is null) break;

            pathParts.Add(nesting.Name);
            current = nesting.Options ?? Enumerable.Empty<SocketSlashCommandDataOption>();
        }

        var leaves = current
            .Where(o => o.Type != ApplicationCommandOptionType.SubCommand
                     && o.Type != ApplicationCommandOptionType.SubCommandGroup)
            .ToList();

        return (string.Join(" ", pathParts), leaves);
    }

    /// <summary>
    /// Returns a JSON object of {param: value} for the allowlisted leaf
    /// options of this command, or null if the command isn't in the
    /// allowlist or no allowlisted params were passed.
    /// </summary>
    private static string? BuildParametersJson(
        string commandName,
        IReadOnlyList<SocketSlashCommandDataOption> leafOptions)
    {
        if (leafOptions.Count == 0) return null;
        if (!ParameterAllowlist.TryGetValue(commandName, out var allowed)) return null;

        var dict = new Dictionary<string, object?>();
        foreach (var opt in leafOptions)
        {
            if (allowed.Contains(opt.Name))
                dict[opt.Name] = opt.Value;
        }

        return dict.Count == 0 ? null : JsonSerializer.Serialize(dict);
    }
}
