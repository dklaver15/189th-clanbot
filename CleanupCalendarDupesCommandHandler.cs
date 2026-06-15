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
    private readonly DiscordSocketClient _client;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<CleanupCalendarDupesCommandHandler> _logger;
    private readonly BotConfig _config;

    public CleanupCalendarDupesCommandHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        GoogleCalendarService calendarService,
        ILogger<CleanupCalendarDupesCommandHandler> logger,
        IOptions<BotConfig> config)
    {
        _services        = services;
        _client          = client;
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
        int ContentGroupsFound,
        int ContentDbRowsRemoved,
        int ContentGCalDeleted,
        int ContentReboundHealed,
        int OrphansFound,
        int OrphansDeleted,
        int OwnedSkipped,
        List<string> Errors,
        List<string> DupeDetail,
        List<string> ContentDetail,
        List<string> OrphanDetail);

    private async Task<CleanupReport> RunCleanupAsync(ulong guildId, bool dryRun)
    {
        var errors       = new List<string>();
        var dupeDetail   = new List<string>();
        var contentDetail = new List<string>();
        var orphanDetail = new List<string>();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // ── In-house ownership guard ──────────────────────────────────
        //
        // CalendarEvents owned by a ClanEvent (the in-house event system) are
        // managed exclusively by ClanGuard's own create/edit/cancel/archive
        // lifecycle and must NEVER be deleted or re-bound here. This command was
        // built for Apollo-sourced rows; deleting an owned row out from under a
        // live ClanEvent orphans it (its #events post + reminders survive while
        // the calendar hub vanishes). Mirrors the same guard in
        // ApolloEventHandler / ApolloReconciliationService. The proper way to
        // collapse duplicate in-house events is /event cancel on the unwanted
        // series, not this command — so we exclude owned rows from every pass.
        var clanOwnedCalIds = new HashSet<int>(
            await db.ClanEvents
                .Where(e => e.GuildId == guildId)
                .Select(e => e.CalendarEventId)
                .ToListAsync());
        int ownedSkipped = 0;

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

        // Drop any in-house ClanEvent-owned rows from consideration (see guard above).
        var dupeOwned = dupeRows.RemoveAll(c => clanOwnedCalIds.Contains(c.Id));
        ownedSkipped += dupeOwned;

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

        // ── Pass 3: content duplicates (sort-induced) ─────────────────
        //
        // Apollo's /sort deletes every event message and re-posts it under a
        // new ID. Before the rebind fix, each sort created a fresh CalendarEvent
        // row + GCal entry while the prior copy lingered — so one logical event
        // ends up with multiple rows that have DISTINCT DiscordMessageIds and
        // DISTINCT GCal IDs. Pass 1 (groups by DiscordMessageId) can't see these,
        // and Pass 2 (GCal-not-in-DB) can't either, because every copy has its
        // own tracked row. This pass collapses them by content identity.
        //
        // Keeper = the copy bound to the message CURRENTLY live in #events
        // (verified by scanning the channel), so the survivor is the row the
        // running bot keeps in sync. We re-bind that survivor to the live
        // message ID and HEAL its ContentHash from the live parse — both are
        // essential, because these pre-fix rows have an empty ContentHash and
        // would otherwise re-duplicate on the very next /sort. If no live
        // message matches the content, we keep the newest row and leave the
        // orphan question to the reconciler.
        int contentGroups        = 0;
        int contentDbRemoved     = 0;
        int contentGCalDeleted   = 0;
        int contentReboundHealed = 0;

        var liveByContent = await BuildLiveContentMapAsync(guildId);

        var clanFuture = await db.CalendarEvents
            .Where(c => c.GuildId == guildId
                     && c.Source == "Clan"
                     && c.DiscordMessageId != 0
                     && c.StartUtc > DateTime.UtcNow)
            .ToListAsync();

        // Drop any in-house ClanEvent-owned rows so they're never kept, dropped,
        // or re-bound here (see ownership guard above). This is what prevents the
        // command from orphaning a live recurring/one-off in-house event.
        ownedSkipped += clanFuture.RemoveAll(c => clanOwnedCalIds.Contains(c.Id));

        var contentGroupsList = clanFuture
            .GroupBy(c => ContentKey(c.Title, c.StartUtc, c.EndUtc))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in contentGroupsList)
        {
            contentGroups++;

            var rows = group.OrderByDescending(c => c.DiscordMessageId).ToList();

            ulong? liveMsgId = null;
            string? liveHash = null;
            CalendarEvent keeper;

            if (liveByContent.TryGetValue(group.Key, out var live))
            {
                liveMsgId = live.MessageId;
                liveHash  = live.Hash;
                // Prefer the row already bound to the live message; otherwise
                // keep the newest and re-bind it to the live message below.
                keeper = rows.FirstOrDefault(r => r.DiscordMessageId == live.MessageId)
                         ?? rows.First();
            }
            else
            {
                keeper = rows.First(); // no live message → keep newest, leave for reconciler
            }

            var losers = rows.Where(r => r.Id != keeper.Id).ToList();

            contentDetail.Add(
                $"• {keeper.Title} ({keeper.StartUtc:yyyy-MM-dd HH:mm}Z): keep Id={keeper.Id}" +
                (liveMsgId is null ? " (no live msg)" : $" (live msg {liveMsgId})") +
                $", drop {losers.Count}");

            // Re-bind + heal the survivor so future sorts dedupe via ContentHash.
            if (liveMsgId is not null)
            {
                if (!dryRun)
                {
                    keeper.DiscordMessageId = liveMsgId.Value;
                    if (!string.IsNullOrEmpty(liveHash)) keeper.ContentHash = liveHash;
                }
                contentReboundHealed++;
            }

            foreach (var row in losers)
            {
                if (!dryRun)
                {
                    if (!string.IsNullOrEmpty(row.CalendarEventId))
                    {
                        try
                        {
                            await _calendarService.DeleteEventAsync(row.CalendarEventId);
                            contentGCalDeleted++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"GCal delete failed for content-dupe {row.CalendarEventId}: {ex.Message}");
                            _logger.LogWarning(ex,
                                "Cleanup: GCal delete failed for content-dupe row Id={Id}", row.Id);
                        }
                    }

                    db.CalendarEvents.Remove(row);
                    contentDbRemoved++;
                }
                else
                {
                    contentDbRemoved++;
                    if (!string.IsNullOrEmpty(row.CalendarEventId)) contentGCalDeleted++;
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
            ContentGroupsFound:         contentGroups,
            ContentDbRowsRemoved:       contentDbRemoved,
            ContentGCalDeleted:         contentGCalDeleted,
            ContentReboundHealed:       contentReboundHealed,
            OrphansFound:               orphans.Count,
            OrphansDeleted:             orphansDeleted,
            OwnedSkipped:               ownedSkipped,
            Errors:                     errors,
            DupeDetail:                 dupeDetail,
            ContentDetail:              contentDetail,
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
        sb.AppendLine("**Pass 3 — Content duplicates (sort-induced)**");
        sb.AppendLine($"Groups found: **{r.ContentGroupsFound}**");
        sb.AppendLine($"DB rows {(dryRun ? "would be " : "")}removed: **{r.ContentDbRowsRemoved}**");
        sb.AppendLine($"GCal events {(dryRun ? "would be " : "")}deleted: **{r.ContentGCalDeleted}**");
        sb.AppendLine($"Survivors {(dryRun ? "would be " : "")}re-bound/healed: **{r.ContentReboundHealed}**");
        if (r.ContentDetail.Count > 0)
        {
            sb.AppendLine();
            foreach (var line in r.ContentDetail.Take(10))
                sb.AppendLine(line);
            if (r.ContentDetail.Count > 10)
                sb.AppendLine($"... and {r.ContentDetail.Count - 10} more");
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

        if (r.OwnedSkipped > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"🛡️ **Skipped {r.OwnedSkipped} in-house event row(s)** owned by a ClanEvent — " +
                          "these are managed by ClanGuard's own lifecycle. To remove duplicate in-house " +
                          "events, cancel the unwanted series/event with `/event cancel` instead.");
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
    /// Content identity key for grouping/matching duplicate rows. Kind-agnostic
    /// fixed format so a stored CalendarEvent (DateTime may be Unspecified) and a
    /// freshly-parsed live message (may be Utc) produce the same key for the same
    /// wall-clock instant.
    /// </summary>
    private static string ContentKey(string title, DateTime start, DateTime end)
        => $"{title}|{start:yyyy-MM-ddTHH:mm:ss}|{end:yyyy-MM-ddTHH:mm:ss}";

    /// <summary>
    /// Scans the live #events channel and maps each parseable Apollo event's
    /// content key to (live message id, content hash). Used by Pass 3 to pick
    /// the survivor bound to the message that's actually still in the channel,
    /// and to heal that survivor's ContentHash. Best-effort: any failure yields
    /// an empty map and keeper selection falls back to the newest row.
    /// </summary>
    private async Task<Dictionary<string, (ulong MessageId, string Hash)>> BuildLiveContentMapAsync(ulong guildId)
    {
        var map = new Dictionary<string, (ulong, string)>();

        try
        {
            var guild = _client.GetGuild(guildId);
            if (guild is null) return map;

            var channel = ResolveEventsChannel(guild);
            if (channel is null) return map;

            var messages = await channel.GetMessagesAsync(200).FlattenAsync();

            foreach (var message in messages)
            {
                if (!message.Author.IsBot) continue;
                if (!message.Author.Username.Contains(
                        _config.ApolloBotName, StringComparison.OrdinalIgnoreCase)) continue;

                var embed = message.Embeds.FirstOrDefault();
                if (embed is null) continue;

                var parsed = ApolloEmbedParser.Parse(embed);
                if (parsed is null) continue;

                var key = ContentKey(parsed.Title, parsed.StartUtc, parsed.EndUtc);

                // Newest live message wins if two live copies somehow coexist.
                if (!map.TryGetValue(key, out var existing) || message.Id > existing.Item1)
                    map[key] = (message.Id, ApolloContentHash.Compute(parsed));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Cleanup: failed to scan live channel for content dedupe; keeper selection falls back to newest row");
        }

        return map;
    }

    /// <summary>
    /// Resolves the events text channel for a guild — id-first (rename-proof),
    /// name fallback. Mirrors ApolloReconciliationService.ResolveEventsChannel.
    /// </summary>
    private SocketTextChannel? ResolveEventsChannel(SocketGuild guild)
    {
        if (_config.EventsTextChannelId != 0)
        {
            var byId = guild.GetTextChannel(_config.EventsTextChannelId);
            if (byId is not null) return byId;
        }

        return guild.TextChannels.FirstOrDefault(c =>
            c.Name.Equals(_config.EventsTextChannelName, StringComparison.OrdinalIgnoreCase));
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