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
/// Slash command: /cleanup-calendar-dupes [dry_run]
///
/// Reconciles the CalendarEvents table and the Google Calendar against each
/// other, removing duplicates created by past concurrent-write races between
/// MessageReceived and MessageUpdated for the same Apollo post.
///
/// ── Why this exists ──
/// The 2026-05-04 / 2026-05-05 duplicate-events incident: ApolloEventHandler
/// fired two ProcessApolloMessageAsync invocations in parallel for the same
/// message ID, both saw no existing row, both took the CREATE path, and we
/// ended up with two GCal events and two DB rows for the same Apollo post.
/// The per-message lock added in ApolloEventHandler prevents new occurrences,
/// and the filtered unique index on CalendarEvents.DiscordMessageId backstops
/// it. Neither retroactively cleans up the dupes already on the calendar —
/// that's this command's job.
///
/// ── Two passes ──
///   1. In-DB duplicates: groups CalendarEvents by DiscordMessageId where
///      Source='Clan' AND DiscordMessageId != 0, finds groups with count > 1,
///      keeps the highest Id (matches ApolloBackfillService's existing
///      'keep newest' convention), deletes the others' GCal events and DB
///      rows in lockstep.
///   2. GCal orphans: lists future clan-tagged GCal events, cross-references
///      against CalendarEvents.CalendarEventId, deletes any GCal events
///      with no matching DB row. These are the dupes that ApolloBackfill
///      already cleaned up the DB row for, but whose GCal counterpart was
///      left behind because the backfill cleanup didn't call DeleteEventAsync.
///
/// ── Dry run ──
/// dry_run defaults to true. Run once to see the report, then run with
/// dry_run:false to actually apply changes. Mirrors the convention used by
/// AutoPromotionService.
///
/// ── Permissions ──
/// Officer-or-higher only, gated through HasElevatedPermissions which mirrors
/// the same check in SlashCommandHandler (ManageRoles or Administrator, or
/// any role in BotConfig.GetExemptRolesList()).
/// </summary>
public class CleanupCalendarDupesCommandHandler
{
    public const string CommandName = "cleanup-calendar-dupes";

    /// <summary>
    /// How far ahead to scan for orphan GCal events. 90 days comfortably covers
    /// any realistic future-event window the clan posts ahead of time, while
    /// keeping the API call bounded. Past events are not scanned because the
    /// post-event Apollo cleanup policy means most of them have legitimately
    /// been disconnected from a Discord message and aren't actually orphans.
    /// </summary>
    private static readonly TimeSpan OrphanScanWindow = TimeSpan.FromDays(90);

    private readonly IServiceProvider _services;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<CleanupCalendarDupesCommandHandler> _logger;
    private readonly BotConfig _config;

    public CleanupCalendarDupesCommandHandler(
        IServiceProvider services,
        GoogleCalendarService calendarService,
        ILogger<CleanupCalendarDupesCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services        = services;
        _calendarService = calendarService;
        _logger          = logger;
        _config          = config.Value;
    }

    /// <summary>Wire up on the Discord client (call from DiscordBotService startup).</summary>
    public void Register(DiscordSocketClient client)
    {
        client.SlashCommandExecuted += HandleCommandAsync;
    }

    /// <summary>
    /// Slash command shape. Add this to wherever the bot bulk-registers
    /// commands with Discord at startup, alongside the other command builders.
    /// </summary>
    public static SlashCommandProperties BuildCommand() =>
        new SlashCommandBuilder()
            .WithName(CommandName)
            .WithDescription("Reconcile CalendarEvents and Google Calendar; remove duplicate calendar entries.")
            .AddOption(
                "dry_run",
                ApplicationCommandOptionType.Boolean,
                "If true (default), only reports what would be deleted without making changes.",
                isRequired: false)
            .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
            .Build();

    private async Task HandleCommandAsync(SocketSlashCommand command)
    {
        // Filter — we share the SlashCommandExecuted event with every other
        // *CommandHandler in the bot, so early-return for anything not ours.
        if (command.Data.Name != CommandName) return;

        try
        {
            await HandleCleanupAsync(command);
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

    private async Task HandleCleanupAsync(SocketSlashCommand command)
    {
        if (command.GuildId is null)
        {
            await command.RespondAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var caller = command.User as SocketGuildUser;
        if (caller is null || !HasElevatedPermissions(caller))
        {
            await command.RespondAsync("You don't have permission to use this command.", ephemeral: true);
            return;
        }

        var dryRun = command.Data.Options
            .FirstOrDefault(o => o.Name == "dry_run")?.Value as bool? ?? true;

        // Long-running (Google API calls per dupe + per orphan); defer the
        // response so Discord doesn't time out the 3-second initial-response
        // window. Ephemeral so the report stays in the invoker's view only.
        await command.DeferAsync(ephemeral: true);

        var report = await RunCleanupAsync(command.GuildId.Value, dryRun);

        _logger.LogInformation(
            "Cleanup command complete (caller={Caller}, dryRun={DryRun}): " +
            "{DupeGroups} dupe group(s), {DbRows} DB row(s), {GCalDupes} GCal dupes, " +
            "{Orphans} orphan(s), {Errors} error(s)",
            caller.Username, dryRun,
            report.DupeGroupsFound, report.DbRowsRemoved,
            report.GCalEventsDeletedFromDupes, report.OrphansFound,
            report.Errors.Count);

        await command.FollowupAsync(embed: BuildReportEmbed(report, dryRun), ephemeral: true);
    }

    // ─── Implementation ──────────────────────────────────────────────

    private record CleanupReport(
        int DupeGroupsFound,
        int DbRowsRemoved,
        int GCalEventsDeletedFromDupes,
        int OrphansFound,
        int OrphansDeleted,
        List<string> Errors,
        List<string> DupeDetail,
        List<string> OrphanDetail);

    private async Task<CleanupReport> RunCleanupAsync(ulong guildId, bool dryRun)
    {
        var errors       = new List<string>();
        var dupeDetail   = new List<string>();
        var orphanDetail = new List<string>();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ── Pass 1: in-DB duplicates ──────────────────────────────────
        //
        // Find message IDs with more than one CalendarEvent row, then load
        // all rows for those IDs in a single query. Group them in memory,
        // keep the highest Id (matches ApolloBackfillService's existing
        // 'keep newest' convention), drop the rest.
        var dupeMessageIds = await db.CalendarEvents
            .Where(c => c.GuildId == guildId
                     && c.Source == "Clan"
                     && c.DiscordMessageId != 0)
            .GroupBy(c => c.DiscordMessageId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync();

        var dupeRows = dupeMessageIds.Count == 0
            ? new List<CalendarEvent>()
            : await db.CalendarEvents
                .Where(c => c.GuildId == guildId
                         && c.Source == "Clan"
                         && dupeMessageIds.Contains(c.DiscordMessageId))
                .ToListAsync();

        int dbRowsRemoved        = 0;
        int gcalDeletedFromDupes = 0;

        foreach (var group in dupeRows.GroupBy(c => c.DiscordMessageId))
        {
            var ordered  = group.OrderByDescending(c => c.Id).ToList();
            var keep     = ordered.First();
            var toDelete = ordered.Skip(1).ToList();

            dupeDetail.Add(
                $"• `{group.Key}` — {keep.Title} ({keep.StartUtc:yyyy-MM-dd HH:mm}Z): " +
                $"keep Id={keep.Id}, drop {toDelete.Count}");

            foreach (var row in toDelete)
            {
                if (!dryRun)
                {
                    try
                    {
                        await _calendarService.DeleteEventAsync(row.CalendarEventId);
                        gcalDeletedFromDupes++;
                    }
                    catch (Exception ex)
                    {
                        // Don't bail — the DB row should still be removed even
                        // if the GCal delete fails. Worst case: a stale calendar
                        // entry remains and gets caught by Pass 2 next run.
                        errors.Add($"GCal delete failed for dupe {row.CalendarEventId}: {ex.Message}");
                        _logger.LogWarning(ex,
                            "Cleanup: GCal delete failed for dupe row Id={Id}", row.Id);
                    }

                    db.CalendarEvents.Remove(row);
                    dbRowsRemoved++;
                }
                else
                {
                    // Dry run — count what we would do.
                    dbRowsRemoved++;
                    gcalDeletedFromDupes++;
                }
            }
        }

        if (!dryRun) await db.SaveChangesAsync();

        // ── Pass 2: GCal orphans ──────────────────────────────────────
        //
        // List future clan-tagged GCal events, cross-reference against
        // tracked CalendarEventIds in the DB. Anything in GCal but not in
        // the DB is an orphan from a past race whose DB sibling was already
        // cleaned up by ApolloBackfillService.
        //
        // Important: this happens AFTER Pass 1's SaveChangesAsync (when not
        // dry run), so any CalendarEvents removed in Pass 1 are correctly
        // not in trackedSet. In dry-run we haven't actually removed
        // anything, but Pass 1's GCal deletes are also simulated, so the
        // orphan count remains accurate either way.
        var trackedIds = await db.CalendarEvents
            .Where(c => c.Source == "Clan")
            .Select(c => c.CalendarEventId)
            .ToListAsync();
        var trackedSet = new HashSet<string>(trackedIds);

        var fromUtc = DateTime.UtcNow;
        var toUtc   = DateTime.UtcNow + OrphanScanWindow;
        var liveClanEvents = await _calendarService.ListClanEventsAsync(fromUtc, toUtc);

        var orphans = liveClanEvents
            .Where(e => !trackedSet.Contains(e.Id))
            .ToList();

        int orphansDeleted = 0;
        foreach (var orphan in orphans)
        {
            var startStr = orphan.Start?.DateTimeRaw ?? orphan.Start?.Date ?? "?";
            orphanDetail.Add($"• `{orphan.Id}` — {orphan.Summary} ({startStr})");

            if (!dryRun)
            {
                try
                {
                    await _calendarService.DeleteEventAsync(orphan.Id);
                    orphansDeleted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"GCal delete failed for orphan {orphan.Id}: {ex.Message}");
                    _logger.LogWarning(ex,
                        "Cleanup: GCal delete failed for orphan {EventId}", orphan.Id);
                }
            }
            else
            {
                orphansDeleted++;
            }
        }

        return new CleanupReport(
            DupeGroupsFound:            dupeMessageIds.Count,
            DbRowsRemoved:              dbRowsRemoved,
            GCalEventsDeletedFromDupes: gcalDeletedFromDupes,
            OrphansFound:               orphans.Count,
            OrphansDeleted:             orphansDeleted,
            Errors:                     errors,
            DupeDetail:                 dupeDetail,
            OrphanDetail:               orphanDetail);
    }

    private static Embed BuildReportEmbed(CleanupReport r, bool dryRun)
    {
        var modeLabel = dryRun ? "🔍 Dry Run" : "🧹 Live Cleanup";
        var color = dryRun
            ? Color.Blue
            : (r.Errors.Count > 0 ? Color.Orange : Color.Green);

        var sb = new System.Text.StringBuilder();

        sb.AppendLine("**Pass 1 — In-DB duplicates**");
        sb.AppendLine($"Groups found: **{r.DupeGroupsFound}**");
        sb.AppendLine($"DB rows {(dryRun ? "would be " : "")}removed: **{r.DbRowsRemoved}**");
        sb.AppendLine($"GCal events {(dryRun ? "would be " : "")}deleted: **{r.GCalEventsDeletedFromDupes}**");
        if (r.DupeDetail.Count > 0)
        {
            sb.AppendLine();
            foreach (var line in r.DupeDetail.Take(10))
                sb.AppendLine(line);
            if (r.DupeDetail.Count > 10)
                sb.AppendLine($"... and {r.DupeDetail.Count - 10} more");
        }

        sb.AppendLine();
        sb.AppendLine($"**Pass 2 — GCal orphans (next {(int)OrphanScanWindow.TotalDays} days)**");
        sb.AppendLine($"Orphans found: **{r.OrphansFound}**");
        sb.AppendLine($"GCal events {(dryRun ? "would be " : "")}deleted: **{r.OrphansDeleted}**");
        if (r.OrphanDetail.Count > 0)
        {
            sb.AppendLine();
            foreach (var line in r.OrphanDetail.Take(10))
                sb.AppendLine(line);
            if (r.OrphanDetail.Count > 10)
                sb.AppendLine($"... and {r.OrphanDetail.Count - 10} more");
        }

        if (r.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⚠️ **Errors ({r.Errors.Count})**");
            foreach (var err in r.Errors.Take(5))
                sb.AppendLine($"• {err}");
            if (r.Errors.Count > 5)
                sb.AppendLine($"... and {r.Errors.Count - 5} more (see logs)");
        }

        if (dryRun)
        {
            sb.AppendLine();
            sb.AppendLine("_Re-run with `dry_run:false` to apply these changes._");
        }

        return new EmbedBuilder()
            .WithTitle($"{modeLabel} — Calendar Reconciliation")
            .WithColor(color)
            .WithDescription(sb.ToString())
            .WithFooter("ClanGuard Bot • /cleanup-calendar-dupes")
            .WithTimestamp(DateTimeOffset.UtcNow)
            .Build();
    }

    /// <summary>
    /// Mirrors SlashCommandHandler.HasElevatedPermissions — ManageRoles or
    /// Administrator guild permissions, or any role listed in
    /// BotConfig.GetExemptRolesList(). Kept as a private copy rather than
    /// extracted to a shared helper so this handler stays self-contained
    /// and copy-and-pastable into future cleanup commands.
    /// </summary>
    private bool HasElevatedPermissions(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles || user.GuildPermissions.Administrator)
            return true;

        var exemptRoles = _config.GetExemptRolesList();
        return user.Roles.Any(r => exemptRoles.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}