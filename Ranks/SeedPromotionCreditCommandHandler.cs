using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the /seed-promotion-credit slash command — a one-time operational
/// tool for the transition from the manual promotion tracking spreadsheet to
/// bot-tracked event attendance.
///
/// ── Workflow ──
///   1. An officer manually adds two columns to the roster sheet to the right
///      of the bot-managed columns (any position works since the handler
///      locates them by header text):
///         "Seed Events"  — number of qualifying events the person has
///                          already accumulated at their current rank,
///                          pulled from the manual tracking spreadsheet.
///         "Seed Applied" — bot writes "YES | timestamp" here after applying.
///   2. The officer fills in "Seed Events" for each applicable person.
///   3. An officer at or above PromoteDemoteMinRank runs
///      /seed-promotion-credit.
///   4. For every row where "Seed Events" is a non-negative integer AND
///      "Seed Applied" is empty:
///        • The user is located in the guild by Username (column B).
///        • Their RankHistory row is fetched (or created if missing).
///        • RankHistory.EventsAttendedAtRankBeforeBot is set to the seed value.
///        • RankHistory.SeedAppliedAt is set to the current UTC time.
///        • "YES | yyyy-MM-dd HH:mm UTC" is written to the "Seed Applied"
///          cell so re-runs skip this row (idempotency).
///   5. The officer deletes the two columns from the sheet when the
///      transition is complete. Subsequent roster exports clear only A:N
///      so the columns don't reappear.
///
/// ── Important operational note ──
/// The officer should run this command BEFORE the next nightly roster export
/// (or trigger a manual export AFTER seeding is done). The roster export
/// re-sorts data rows by rank and name; if it runs between when the officer
/// fills in seeds and when this command runs, the seed values in columns
/// O/P will no longer line up with the names in columns A/B, and the
/// applied seeds will land on the wrong users.
/// </summary>
public class SeedPromotionCreditCommandHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SeedPromotionCreditCommandHandler> _logger;
    private readonly BotConfig _config;
    private readonly GoogleSheetsService _sheetsService;

    public SeedPromotionCreditCommandHandler(
        IServiceProvider services,
        ILogger<SeedPromotionCreditCommandHandler> logger,
        IOptions<BotConfig> config,
        GoogleSheetsService sheetsService)
    {
        _services = services;
        _logger = logger;
        _config = config.Value;
        _sheetsService = sheetsService;
    }

    /// <summary>Register on the Discord client.</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        if (command.Data.Name is not "seed-promotion-credit")
            return;

        try
        {
            await HandleSeedAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling /seed-promotion-credit");
            try
            {
                await command.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* already responded */ }
        }
    }

    private async Task HandleSeedAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasSeedPermission(caller))
        {
            await command.FollowupAsync(
                $"❌ You don't have permission to use this command (requires {_config.PromoteDemoteMinRank}+).",
                ephemeral: true);
            return;
        }

        var guild = (command.Channel as SocketGuildChannel)?.Guild;
        if (guild is null)
        {
            await command.FollowupAsync("Could not determine the guild for this command.", ephemeral: true);
            return;
        }

        await command.FollowupAsync("⏳ Reading roster sheet...", ephemeral: true);

        // ── Read the roster sheet ───────────────────────────────────
        var readResult = await _sheetsService.ReadRosterSeedRowsAsync();
        if (!readResult.Success)
        {
            await command.FollowupAsync($"❌ {readResult.Error}", ephemeral: true);
            return;
        }

        var unappliedRows = readResult.Rows
            .Where(r => r.SeedEvents.HasValue && string.IsNullOrWhiteSpace(r.SeedApplied))
            .ToList();

        if (unappliedRows.Count == 0)
        {
            await command.FollowupAsync(
                "No rows to process — every row with a value in 'Seed Events' already has a 'Seed Applied' marker. To re-seed a user, clear their 'Seed Applied' cell and re-run.",
                ephemeral: true);
            return;
        }

        _logger.LogInformation(
            "{Caller} triggered /seed-promotion-credit: {Count} unapplied row(s) found in roster sheet",
            caller.Username, unappliedRows.Count);

        // Ensure the Discord member cache is warm so Username lookups succeed
        await guild.DownloadUsersAsync();

        // ── Apply each seed ─────────────────────────────────────────
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var applied = new List<AppliedSeed>();
        var skipped = new List<SkippedSeed>();
        var appliedCellUpdates = new Dictionary<int, string>();

        var now = DateTime.UtcNow;
        var appliedMarker = $"YES | {now:yyyy-MM-dd HH:mm} UTC";

        foreach (var row in unappliedRows)
        {
            // Locate the Discord user by Username (column B). Username is the
            // unique Discord handle (post-2023 handle system) so this is a
            // safer match than display name.
            var member = guild.Users.FirstOrDefault(u =>
                string.Equals(u.Username, row.Username, StringComparison.OrdinalIgnoreCase));

            if (member is null)
            {
                skipped.Add(new SkippedSeed(row,
                    $"No guild member with username '{row.Username}'"));
                _logger.LogWarning(
                    "Seed skip: row {Row} username '{Username}' not found in guild",
                    row.RowNumber, row.Username);
                continue;
            }

            // Get or create the RankHistory row. If the bot has never seen this
            // member before, seed the record using whatever rank appears in
            // column C of the sheet (falls back to empty string; RankTracking
            // and the roster export will correct it on next run).
            var rankRecord = await db.RankHistories
                .FirstOrDefaultAsync(r => r.GuildId == guild.Id && r.UserId == member.Id);

            if (rankRecord is null)
            {
                rankRecord = new RankHistory
                {
                    GuildId = guild.Id,
                    UserId = member.Id,
                    RankName = row.Rank ?? string.Empty,
                    AssignedAt = member.JoinedAt?.UtcDateTime ?? now,
                };
                db.RankHistories.Add(rankRecord);
                _logger.LogInformation(
                    "Seed: created new RankHistory row for {Username} at rank '{Rank}'",
                    row.Username, row.Rank);
            }

            var previousSeed = rankRecord.EventsAttendedAtRankBeforeBot;
            rankRecord.EventsAttendedAtRankBeforeBot = row.SeedEvents!.Value;
            rankRecord.SeedAppliedAt = now;

            applied.Add(new AppliedSeed(row, member, previousSeed));
            appliedCellUpdates[row.RowNumber] = appliedMarker;
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save seed updates to DB");
            await command.FollowupAsync(
                $"❌ Failed to save seed updates to DB: {ex.Message}\nNo sheet cells were updated. Safe to retry.",
                ephemeral: true);
            return;
        }

        // ── Write back "Seed Applied" markers ───────────────────────
        try
        {
            await _sheetsService.WriteSeedAppliedCellsAsync(
                readResult.SeedAppliedColumnIndex,
                appliedCellUpdates);
        }
        catch (Exception ex)
        {
            // DB changes are already committed. Log the partial-success state
            // loudly so the officer knows to either manually update the sheet
            // or accept that re-running will re-apply these rows.
            _logger.LogError(ex,
                "DB updates succeeded but writing Seed Applied cells failed. {Count} row(s) may get re-applied on next run — clear the affected DB rows' SeedAppliedAt before re-running to avoid duplicate seeding, OR manually fill the Seed Applied cells.",
                appliedCellUpdates.Count);

            await command.FollowupAsync(
                $"⚠️ Applied {applied.Count} seed(s) to the DB, but failed to mark the sheet: {ex.Message}\n" +
                "The seeds ARE applied in the DB. To avoid a re-apply on the next run, manually put any value in the 'Seed Applied' column for the rows that were just processed.",
                ephemeral: true);
            return;
        }

        _logger.LogInformation(
            "/seed-promotion-credit complete: applied={Applied}, skipped={Skipped}",
            applied.Count, skipped.Count);

        // ── Build response embed ────────────────────────────────────
        var embed = BuildResponseEmbed(applied, skipped);
        await command.FollowupAsync(embed: embed, ephemeral: true);
    }

    private Embed BuildResponseEmbed(
        IReadOnlyList<AppliedSeed> applied,
        IReadOnlyList<SkippedSeed> skipped)
    {
        var color = skipped.Count == 0 ? Color.Green : Color.Orange;

        var builder = new EmbedBuilder()
            .WithTitle("🌱 Promotion Credit Seeding")
            .WithColor(color)
            .WithFooter($"Applied at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");

        // Applied section — show up to 15 entries inline, then a "and N more"
        // fallback to keep us well under Discord's embed-size limits.
        if (applied.Count > 0)
        {
            var lines = new List<string>();
            foreach (var a in applied.Take(15))
            {
                var prev = a.PreviousSeed == 0 ? "" : $" (was {a.PreviousSeed})";
                lines.Add($"• `{a.Row.Username}` — {a.Row.Rank}: **{a.Row.SeedEvents}**{prev}");
            }
            if (applied.Count > 15)
                lines.Add($"…and {applied.Count - 15} more.");

            builder.AddField($"✅ Applied ({applied.Count})", string.Join("\n", lines));
        }

        if (skipped.Count > 0)
        {
            var lines = new List<string>();
            foreach (var s in skipped.Take(15))
            {
                lines.Add($"• row {s.Row.RowNumber} (`{s.Row.Username}`): {s.Reason}");
            }
            if (skipped.Count > 15)
                lines.Add($"…and {skipped.Count - 15} more.");

            builder.AddField($"⚠️ Skipped ({skipped.Count})", string.Join("\n", lines));
        }

        if (applied.Count == 0 && skipped.Count == 0)
        {
            builder.WithDescription("No rows were processed.");
        }

        return builder.Build();
    }

    /// <summary>
    /// Same rank-based permission model as /promote and /demote: the caller
    /// must be at or above the configured PromoteDemoteMinRank (or be a
    /// server Administrator).
    /// </summary>
    private bool HasSeedPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minRank = _config.PromoteDemoteMinRank;
        var minIndex = rankRoles.FindIndex(r => r.Equals(minRank, StringComparison.OrdinalIgnoreCase));

        if (minIndex < 0) return false;

        return user.Roles.Any(role =>
        {
            var roleIndex = rankRoles.FindIndex(r => r.Equals(role.Name, StringComparison.OrdinalIgnoreCase));
            return roleIndex >= minIndex;
        });
    }

    private record AppliedSeed(
        GoogleSheetsService.RosterSeedRow Row,
        SocketGuildUser Member,
        int PreviousSeed);

    private record SkippedSeed(
        GoogleSheetsService.RosterSeedRow Row,
        string Reason);
}