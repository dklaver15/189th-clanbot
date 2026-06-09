using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// One-shot startup service that reconciles the CalendarEvents table against
/// the actual state of the #events Discord channel. Detects and cleans up
/// CalendarEvent rows whose source Apollo message is no longer present in
/// the channel — typically because the bot missed a MessageDeleted gateway
/// event while disconnected or restarting.
///
/// ── Why this exists ──
/// ApolloEventHandler.OnMessageDeleted handles live deletions correctly,
/// but Discord doesn't replay missed gateway events on reconnect. If a
/// member deletes their Apollo event post while the bot is down, the
/// CalendarEvent row stays behind forever — alongside its Google Calendar
/// entry — causing stale calendar entries and double-counted EventAttendance
/// rows when EventAttendanceSnapshotService eventually runs against the
/// orphan event.
///
/// The 2026-04-27 HellDivers incident exposed this: an officer created an
/// Apollo event with a wrong time, deleted it, then recreated it. The bot
/// saw both creates but missed the delete, so two CalendarEvent rows
/// existed for the same real-world event. Both got snapshotted on
/// 2026-05-02, double-counting attendance for everyone who showed up. The
/// dup CalendarEvent and its 7 dup EventAttendance rows had to be cleaned
/// up by hand via sqlite.
///
/// ── How it works ──
/// At startup, after the Discord client is ready and the other Apollo-
/// adjacent startup services have had a chance to settle, we walk all
/// CalendarEvent rows with Source = "Clan" within the lookback window
/// (plus all future-dated events) and try to fetch each row's source
/// Apollo message via channel.GetMessageAsync. If the message returns
/// null or the API replies 404, the source post is gone and we treat
/// it the same way ApolloEventHandler.OnMessageDeleted would: delete
/// the Google Calendar entry, delete the CalendarEvent row, log it.
///
/// ── Why EventAttendance rows are NOT deleted here ──
/// Same policy as the live deletion handler: EventAttendance is durable
/// historical data and survives CalendarEvent deletion by design. For
/// the 2026-04-27 case, the duplicate EventAttendance rows had to be
/// cleaned up manually because the snapshot ran before this service
/// existed. Going forward, this service runs early enough at startup
/// that orphan CalendarEvents are gone before
/// EventAttendanceSnapshotService's catch-up pass — so the duplicate-
/// attendance scenario can't recur even though we leave EventAttendance
/// alone here.
///
/// ── Lookback window ──
/// We only check CalendarEvent rows created within the last
/// LookbackWindow days. This bounds the API cost (one Discord fetch
/// per row) and matches the realistic failure mode: missed deletions
/// happen during recent restarts, not from years ago. Old orphans, if
/// any, are mostly harmless after the fact (they just sit in the
/// calendar forever) and can be cleaned up manually if anyone notices.
///
/// Future-dated events are included regardless of CreatedAt, since
/// deletes can happen anytime up to the event running.
///
/// ── Startup ordering ──
/// EventAttendanceSnapshotService waits 30 seconds after Discord
/// readiness before running its startup catch-up pass. This service
/// waits 10 seconds, runs reconciliation, then exits. That gives a
/// comfortable margin: orphan CalendarEvents are removed well before
/// the snapshot service tries to compute attendance against them.
/// </summary>
public class ApolloReconciliationService : BackgroundService
{
    /// <summary>
    /// How far back to look at CalendarEvent.CreatedAt. Events older than
    /// this are skipped to bound the per-row Discord API cost at startup.
    /// Future-dated events bypass this limit since they can be deleted
    /// up until they run.
    ///
    /// 60 days is generous on top of Apollo's typical ~30-day auto-cleanup
    /// — enough to catch deletions the bot missed while down, without
    /// burning startup time fetching ancient messages that almost
    /// certainly no longer exist for unrelated reasons.
    /// </summary>
    private static readonly TimeSpan LookbackWindow = TimeSpan.FromDays(60);

    /// <summary>
    /// Pause between Discord message fetches. Discord's per-channel fetch
    /// rate limit is generous (~50/sec) but we space fetches modestly to
    /// stay well under it and avoid contributing to gateway pressure
    /// during the bot's startup window. With this spacing, even 100
    /// candidate events finish in well under 30 seconds.
    /// </summary>
    private static readonly TimeSpan FetchSpacing = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Time to wait after Discord readiness before running. Trades off
    /// "give other startup services time to settle" against "finish
    /// before EventAttendanceSnapshotService starts its catch-up pass."
    /// Snapshot service waits 30 seconds; we wait 10, leaving comfortable
    /// margin for the actual reconciliation work.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Source value used for clan-sourced (Apollo) events. CompDiv events
    /// don't go through Apollo and aren't reconcilable here — they have
    /// no source message to fetch.
    /// </summary>
    private const string ClanEventSource = "Clan";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<ApolloReconciliationService> _logger;
    private readonly BotConfig _config;

    public ApolloReconciliationService(
        IServiceProvider services,
        DiscordSocketClient client,
        GoogleCalendarService calendarService,
        ILogger<ApolloReconciliationService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client = client;
        _calendarService = calendarService;
        _logger = logger;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord readiness — we need to fetch messages by ID, which
        // only works once the gateway is connected and we have guild handles.
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        // Buffer so ApolloBackfillService has a moment to finish inserting
        // any CalendarEvent rows it discovers from history scan. We don't
        // want to remove a row that backfill is about to confirm.
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            await RunReconciliationAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apollo reconciliation pass failed");
        }
    }

    private async Task RunReconciliationAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - LookbackWindow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Recent past events + all future events. The future-events arm is
        // important: a member can post an event for next week, then delete
        // it the same day while the bot is restarting. Without future
        // coverage, that delete would be missed permanently.
        //
        // Source filter excludes CompDiv events, which are bot-generated
        // (DiscordMessageId == 0) and don't have an Apollo source message
        // to reconcile against.
        //
        // The owning-ClanEvent exclusion is load-bearing: the in-house event
        // system (EventPublisher) writes CalendarEvents with the SAME
        // Source="Clan" value, but posts its RSVP message to
        // GetEventPostChannelId() (EventPostChannelId — often a dedicated test
        // channel), NOT the Apollo EventsTextChannelId that ResolveEventsChannel
        // scans below. Without this guard, every in-house event looks like an
        // orphaned Apollo post — its message is "missing" only because we're
        // looking in the wrong channel — and the future-events arm deletes the
        // hub row, orphaning the ClanEvent + outbox and silently dropping the
        // event from Google Calendar. A CalendarEvent owned by a ClanEvent is
        // managed by ClanGuard's own create/edit/cancel/archive lifecycle and
        // must never be reconciled here. Apollo-sourced events never have an
        // owning ClanEvent, so Apollo reconciliation is unaffected.
        var candidates = await db.CalendarEvents
            .Where(c => c.Source == ClanEventSource
                     && (c.CreatedAt >= cutoff || c.StartUtc > now)
                     && !db.ClanEvents.Any(e => e.CalendarEventId == c.Id))
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogInformation("Apollo reconciliation: no CalendarEvent rows in scope");
            return;
        }

        _logger.LogInformation(
            "Apollo reconciliation: checking {Count} CalendarEvent row(s) against #events (lookback {Days}d)",
            candidates.Count, (int)LookbackWindow.TotalDays);

        var totalChecked       = 0;
        var totalRemoved       = 0;
        var totalRebound       = 0;
        var totalPreserved     = 0;
        var totalChannelMissing = 0;
        var totalFetchErrors   = 0;

        // Group by guild so we resolve the channel once per guild rather
        // than per row. Most clans run a single guild; the grouping is
        // mostly structural.
        foreach (var byGuild in candidates.GroupBy(c => c.GuildId))
        {
            if (ct.IsCancellationRequested) break;

            var guild = _client.GetGuild(byGuild.Key);
            if (guild is null)
            {
                _logger.LogWarning(
                    "Apollo reconciliation: bot not in guild {GuildId}, skipping {Count} row(s)",
                    byGuild.Key, byGuild.Count());
                continue;
            }

            var channel = ResolveEventsChannel(guild);
            if (channel is null)
            {
                _logger.LogWarning(
                    "Apollo reconciliation: events channel not found in guild '{GuildName}', skipping {Count} row(s)",
                    guild.Name, byGuild.Count());
                totalChannelMissing += byGuild.Count();
                continue;
            }

            // Build a content-hash → live-message-id map of what's currently in
            // the channel. Lets us tell a genuinely-deleted event apart from one
            // that was merely re-posted under a new ID (a /sort that happened
            // while the bot was down, so we never saw the live delete+repost).
            // Best-effort: on any failure we get an empty map and fall back to
            // the pre-existing delete-orphan behaviour.
            var liveByHash = await BuildLiveContentHashMapAsync(channel, ct);

            foreach (var row in byGuild)
            {
                if (ct.IsCancellationRequested) break;

                totalChecked++;

                // Defensive: CompDiv events use DiscordMessageId = 0 and
                // shouldn't be in this list given the Source filter above,
                // but the guard is cheap and makes the intent explicit.
                if (row.DiscordMessageId == 0) continue;

                bool messageGone;
                try
                {
                    var fetched = await channel.GetMessageAsync(row.DiscordMessageId);
                    messageGone = fetched is null;
                }
                catch (HttpException ex) when (ex.HttpCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Discord returns 404 for messages that have been
                    // deleted and aren't in any cache. Same outcome as null.
                    messageGone = true;
                }
                catch (Exception ex)
                {
                    // Any other error (rate limit, transient network blip,
                    // permissions change) → skip defensively. Better to
                    // leave a stale CalendarEvent than to delete one
                    // because of a temporary fetch failure.
                    _logger.LogWarning(ex,
                        "Apollo reconciliation: fetch failed for message {MessageId} (CalendarEvent Id={Id}); skipping",
                        row.DiscordMessageId, row.Id);
                    totalFetchErrors++;
                    continue;
                }

                if (messageGone)
                {
                    // Before deleting, check whether this event is simply alive
                    // under a new message ID — i.e. it was re-posted (sorted)
                    // while we were down. Match on the stored content hash;
                    // empty hashes (pre-migration rows) never match and fall
                    // through to the orphan delete, preserving old behaviour.
                    //
                    // Guard: only re-bind if no OTHER row already tracks that
                    // live message. If one does — e.g. a re-post already created
                    // its own CalendarEvent before this old message's deletion
                    // was reconciled — then THIS row is the stale duplicate.
                    // Delete it instead of re-binding, which would otherwise
                    // collide on the unique DiscordMessageId index.
                    var rebound = false;
                    if (!string.IsNullOrEmpty(row.ContentHash)
                        && liveByHash.TryGetValue(row.ContentHash, out var liveId)
                        && liveId != row.DiscordMessageId
                        && !await db.CalendarEvents.AnyAsync(c => c.DiscordMessageId == liveId, ct))
                    {
                        var oldId = row.DiscordMessageId;
                        row.DiscordMessageId = liveId;
                        await db.SaveChangesAsync(ct);
                        totalRebound++;
                        rebound = true;

                        _logger.LogInformation(
                            "Apollo reconciliation: re-bound CalendarEvent Id={Id} '{Title}' from missing message {OldId} to live re-post {NewId} (likely /sort during downtime)",
                            row.Id, row.Title, oldId, liveId);
                    }

                    if (!rebound)
                    {
                        if (row.EndUtc > DateTime.UtcNow)
                        {
                            // Pre-event orphan: the event hadn't run yet and its
                            // message is gone with no rebindable re-post — a
                            // genuine cancellation (or a stale sort duplicate).
                            await DeleteOrphanAsync(db, row);
                            totalRemoved++;
                        }
                        else
                        {
                            // Post-event orphan: message removed after the event
                            // ran (routine channel cleanup). Preserve the row and
                            // GCal entry as historical record — matches the
                            // tombstone path. Deleting here would silently erase
                            // calendar history on every restart.
                            totalPreserved++;
                            _logger.LogDebug(
                                "Apollo reconciliation: preserving past orphan CalendarEvent Id={Id} '{Title}' (EndUtc {End:yyyy-MM-dd HH:mm} UTC) as historical record",
                                row.Id, row.Title, row.EndUtc);
                        }
                    }
                }

                // Brief spacing to avoid bursting the channel's fetch rate
                // limit during a startup with many candidates.
                try { await Task.Delay(FetchSpacing, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation(
            "Apollo reconciliation complete: {Checked} checked, {Rebound} re-bound (re-posted), {Removed} orphan(s) removed, {Preserved} past orphan(s) preserved, {ChannelMissing} skipped (channel missing), {FetchErrors} skipped (fetch errors)",
            totalChecked, totalRebound, totalRemoved, totalPreserved, totalChannelMissing, totalFetchErrors);
    }

    /// <summary>
    /// Mirror of ApolloEventHandler.HandleMessageDeletedAsync's deletion
    /// logic. Drops the Google Calendar entry and the CalendarEvents row.
    /// EventAttendance rows are intentionally left in place — they're
    /// durable historical data per the table's design contract.
    /// </summary>
    private async Task DeleteOrphanAsync(BotDbContext db, CalendarEvent row)
    {
        try
        {
            await _calendarService.DeleteEventAsync(row.CalendarEventId);
        }
        catch (Exception ex)
        {
            // Don't bail — the DB row should still be removed even if the
            // Google Calendar API delete fails. Worst case: a stale
            // calendar entry remains and an officer can clean it up
            // manually. Logged so it's visible.
            _logger.LogWarning(ex,
                "Apollo reconciliation: Google Calendar delete failed for event {CalendarEventId} ('{Title}'); removing DB row anyway",
                row.CalendarEventId, row.Title);
        }

        db.CalendarEvents.Remove(row);
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Apollo reconciliation: removed orphan CalendarEvent Id={Id} '{Title}' (messageId={MessageId}, start={Start:yyyy-MM-dd HH:mm} UTC)",
            row.Id, row.Title, row.DiscordMessageId, row.StartUtc);
    }

    /// <summary>
    /// Scans the current messages in the events channel and maps each parseable
    /// Apollo event's content hash to its live message ID. Used to detect an
    /// event that was re-posted (sorted) under a new ID while the bot was down,
    /// so reconciliation re-binds it instead of deleting it as an orphan.
    ///
    /// Best-effort: any failure (rate limit, permissions, parse) yields an empty
    /// or partial map, and the caller falls back to the orphan-delete path.
    /// Bounded fetch (200 messages) comfortably covers Apollo's per-channel event
    /// cap (10 free / 50 premium) plus headroom.
    /// </summary>
    private async Task<Dictionary<string, ulong>> BuildLiveContentHashMapAsync(
        SocketTextChannel channel, CancellationToken ct)
    {
        var map = new Dictionary<string, ulong>();

        try
        {
            var messages = await channel.GetMessagesAsync(200).FlattenAsync();

            foreach (var message in messages)
            {
                if (ct.IsCancellationRequested) break;

                if (!message.Author.IsBot) continue;
                if (!message.Author.Username.Contains(
                        _config.ApolloBotName, StringComparison.OrdinalIgnoreCase)) continue;

                var embed = message.Embeds.FirstOrDefault();
                if (embed is null) continue;

                var parsed = ApolloEmbedParser.Parse(embed);
                if (parsed is null) continue;

                // First writer wins; in-channel content duplicates are
                // vanishingly unlikely and don't affect correctness — we just
                // re-bind to whichever live copy we saw first.
                map.TryAdd(ApolloContentHash.Compute(parsed), message.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Apollo reconciliation: failed to scan live channel for rebind matching; proceeding with orphan deletes only");
        }

        return map;
    }

    /// <summary>
    /// Resolves the events text channel for a guild. Mirrors the lookup
    /// logic in ApolloEventHandler.IsEventsChannel — id-first (rename-
    /// proof), name fallback for backwards compatibility.
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
}