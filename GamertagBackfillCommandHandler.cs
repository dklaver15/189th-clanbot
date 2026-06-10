using System.Text;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// /gamertag-backfill-ids — a maintenance command that fills in the Discord ID
/// (column A) on legacy roster rows that don't have one, so the ID-keyed
/// gamertag reconciler can manage them.
///
/// ── Why a command, not a background job ──
/// Matching a row to a member by name is inherently fuzzy (display names collide
/// and change), and writing an ID asserts "this row belongs to this person." So
/// this is officer-run and **dry-run by default**: it reports every proposed
/// match for review and only writes when invoked with `apply:true`. It fills a
/// row ONLY when the name maps to exactly one current guild member; ambiguous and
/// unmatched rows are reported and left untouched.
///
/// ── Safety ──
/// Only column A is ever written (see GoogleSheetsService.BackfillGamertagIdsAsync)
/// — names and tags are never touched. Gated to Administrators / the configured
/// GamertagSetupRoleId, same as /setup-gamertags.
/// </summary>
public sealed class GamertagBackfillCommandHandler
{
    public const string CommandName = "gamertag-backfill-ids";

    private const int MaxListedPerSection = 15;

    private readonly GoogleSheetsService _sheets;
    private readonly BotConfig _config;
    private readonly ILogger<GamertagBackfillCommandHandler> _logger;

    public GamertagBackfillCommandHandler(
        GoogleSheetsService sheets,
        IOptions<BotConfig> config,
        ILogger<GamertagBackfillCommandHandler> logger)
    {
        _sheets  = sheets;
        _config  = config.Value;
        _logger  = logger;
    }

    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Fill missing Discord IDs on legacy gamertag rows (officers only)")
            .AddOption("apply", ApplicationCommandOptionType.Boolean,
                "Actually write the IDs. Leave off for a dry-run preview.", isRequired: false)
            .Build();

    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += OnSlashCommandAsync;
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /{Command}", CommandName);
            try
            {
                if (command.HasResponded)
                    await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                else
                    await command.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("This command must be used inside a server.", ephemeral: true);
            return;
        }

        var invoker = guild.GetUser(command.User.Id);
        if (invoker is null || !InvokerHasPermission(invoker))
        {
            await command.FollowupAsync("⛔ You don't have permission to run this command.", ephemeral: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.GoogleSpreadsheetId))
        {
            await command.FollowupAsync("⚠️ No roster spreadsheet is configured.", ephemeral: true);
            return;
        }

        var apply = (bool)(command.Data.Options.FirstOrDefault(o => o.Name == "apply")?.Value ?? false);

        // Ensure the full member list is loaded before matching.
        await guild.DownloadUsersAsync();
        var members = guild.Users.Where(u => !u.IsBot).ToList();

        var legacy = await _sheets.GetLegacyGamertagRowsAsync();
        if (legacy.Count == 0)
        {
            await command.FollowupAsync("✅ No legacy rows found — every roster row already has a Discord ID.", ephemeral: true);
            return;
        }

        var proposed  = new Dictionary<int, ulong>();      // rowNumber → memberId
        var proposedL = new List<string>();
        var ambiguous = new List<string>();
        var unmatched = new List<string>();

        foreach (var row in legacy)
        {
            var display = !string.IsNullOrWhiteSpace(row.ColB) ? row.ColB : row.ColA;

            // Candidate names from the first two cells (covers "name in A" legacy
            // rows and "name in B" modern-shaped rows alike).
            var candidates = new[] { row.ColA, row.ColB }
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToArray();

            var matches = members.Where(m => MatchesAnyName(m, candidates)).ToList();

            if (matches.Count == 1)
            {
                proposed[row.RowNumber] = matches[0].Id;
                proposedL.Add($"Row {row.RowNumber}: `{display}` → <@{matches[0].Id}>");
            }
            else if (matches.Count > 1)
            {
                ambiguous.Add($"Row {row.RowNumber}: `{display}` → {matches.Count} members match");
            }
            else
            {
                unmatched.Add($"Row {row.RowNumber}: `{display}`");
            }
        }

        var written = 0;
        if (apply && proposed.Count > 0)
            written = await _sheets.BackfillGamertagIdsAsync(proposed);

        var embed = BuildReport(apply, legacy.Count, proposed.Count, written, proposedL, ambiguous, unmatched);
        await command.FollowupAsync(embed: embed, ephemeral: true);

        _logger.LogInformation(
            "Gamertag backfill by {Invoker} (apply={Apply}): {Legacy} legacy rows, {Proposed} matched, " +
            "{Ambiguous} ambiguous, {Unmatched} unmatched, {Written} written",
            command.User.Username, apply, legacy.Count, proposed.Count, ambiguous.Count, unmatched.Count, written);
    }

    private static bool MatchesAnyName(SocketGuildUser member, IEnumerable<string> candidates)
    {
        var fields = new[] { member.Username, member.GlobalName, member.Nickname, member.DisplayName }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToArray();

        return candidates.Any(c => fields.Any(f => string.Equals(f, c, StringComparison.OrdinalIgnoreCase)));
    }

    private static Embed BuildReport(
        bool apply, int legacyCount, int matchedCount, int written,
        List<string> proposed, List<string> ambiguous, List<string> unmatched)
    {
        var eb = new EmbedBuilder()
            .WithTitle(apply ? "🛠️ Gamertag ID Backfill — Applied" : "🔍 Gamertag ID Backfill — Dry Run")
            .WithColor(apply ? Color.Green : Color.Blue)
            .WithDescription(apply
                ? $"Found **{legacyCount}** legacy row(s). Wrote **{written}** Discord ID(s); ambiguous and unmatched rows were left untouched."
                : $"Found **{legacyCount}** legacy row(s). **{matchedCount}** have a unique match and would be filled. Re-run with `apply:true` to write them.");

        AddSection(eb, apply ? "✅ Filled" : "✅ Will fill", proposed);
        AddSection(eb, "⚠️ Ambiguous (skipped)", ambiguous);
        AddSection(eb, "❓ No match (skipped)", unmatched);

        eb.WithFooter("Only column A (Discord ID) is written — names and tags are never changed.");
        return eb.Build();
    }

    private static void AddSection(EmbedBuilder eb, string title, List<string> lines)
    {
        if (lines.Count == 0) return;

        var shown = lines.Take(MaxListedPerSection).ToList();
        var sb = new StringBuilder(string.Join("\n", shown));
        if (lines.Count > shown.Count)
            sb.Append($"\n…and {lines.Count - shown.Count} more");

        var value = sb.ToString();
        if (value.Length > 1024) value = value[..1021] + "…"; // embed field cap

        eb.AddField($"{title} ({lines.Count})", value);
    }

    private bool InvokerHasPermission(SocketGuildUser invoker)
    {
        if (invoker.GuildPermissions.Administrator) return true;
        if (_config.GamertagSetupRoleId == 0) return false;
        return invoker.Roles.Any(r => r.Id == _config.GamertagSetupRoleId);
    }
}
