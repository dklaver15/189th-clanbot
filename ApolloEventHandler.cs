using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Monitors the configured events text channel for posts from the Apollo bot.
///
/// For each Apollo event embed, this handler:
///   1. Parses the event title, start/end time, description, and organizer via ApolloEmbedParser.
///   2. Checks Google Calendar for any overlapping events.
///   3. If there is an overlap, DMs the organizer with the conflict details.
///   4. Adds the event to Google Calendar (tagged "Clan", teal color).
///   5. Persists a CalendarEvent DB record so edits/deletes stay in sync.
///
/// MessageUpdated — updates the calendar event (title, times, AND description) if Apollo edits.
/// MessageDeleted — removes the calendar event ONLY when the deletion happens
///   BEFORE the event runs (treated as cancellation). Post-event deletions
///   are clan policy: Apollo messages are routinely removed from #events to
///   keep the channel clean once an event has ended. The calendar entry
///   persists as historical record. See HandleMessageDeletedAsync for the
///   full reasoning.
///
/// Channel identification prefers EventsTextChannelId (numeric, rename-proof) and
/// falls back to EventsTextChannelName for backwards compatibility.
/// </summary>
public class ApolloEventHandler
{
    /// <summary>
    /// Per-message serialization gate.
    ///
    /// Discord's gateway can fire MessageReceived and MessageUpdated for the
    /// same Apollo post within a few hundred milliseconds — a user RSVPing
    /// immediately after the post goes up triggers Apollo to re-render the
    /// embed, which arrives as a MessageUpdated. Both events are dispatched
    /// fire-and-forget into ProcessApolloMessageAsync, each opening its own
    /// DbContext scope.
    ///
    /// The 2026-05-04 / 2026-05-05 duplicate-calendar-events incident traced
    /// to this race: both invocations queried CalendarEvents by
    /// DiscordMessageId, both saw no row (the first hadn't committed yet),
    /// both took the CREATE path, and we ended up with two GCal events and
    /// two DB rows pointing at the same Apollo message. The Hardcore Rush
    /// (2026-05-08) and Among Us (2026-05-09) duplicates were created
    /// exactly this way — CreatedAt timestamps in the same wall-clock second.
    ///
    /// This semaphore serializes processing per message ID. A second
    /// invocation for the same message waits for the first to commit before
    /// running its own lookup. The lookup then finds the first row and
    /// either takes the UPDATE path or skips, depending on isUpdate.
    ///
    /// Combined with the filtered unique index on CalendarEvents
    /// (DiscordMessageId) WHERE DiscordMessageId != 0, this is belt-and-
    /// suspenders: the lock is the primary defense, the index is the
    /// backstop in case a future change ever introduces a code path that
    /// bypasses the lock.
    ///
    /// ── Memory ──
    /// Entries are never removed. Each SemaphoreSlim is ~150 bytes; even
    /// after 10k events the dictionary footprint is well under 2 MB. The
    /// bot is restarted often enough that unbounded growth isn't a real
    /// concern, and removing entries opens a re-entrancy window where a
    /// late-arriving update for a pruned key could race a fresh insert
    /// for the same key.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _messageLocks = new();

    private readonly IServiceProvider _services;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<ApolloEventHandler> _logger;
    private readonly BotConfig _config;

    public ApolloEventHandler(
        IServiceProvider services,
        GoogleCalendarService calendarService,
        ILogger<ApolloEventHandler> logger,
        IOptions<BotConfig> config)
    {
        _services        = services;
        _calendarService = calendarService;
        _logger          = logger;
        _config          = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceived;
        client.MessageUpdated  += OnMessageUpdated;
        client.MessageDeleted  += OnMessageDeleted;
    }

    // ─── Event Handlers ──────────────────────────────────────────────

    private Task OnMessageReceived(SocketMessage message)
    {
        if (!IsApolloMessage(message)) return Task.CompletedTask;
        if (message.Channel is not SocketTextChannel textChannel) return Task.CompletedTask;
        if (!IsEventsChannel(textChannel)) return Task.CompletedTask;

        _ = ProcessApolloMessageAsync(message, textChannel.Guild, isUpdate: false);
        return Task.CompletedTask;
    }

    private Task OnMessageUpdated(
        Cacheable<IMessage, ulong> _before,
        SocketMessage after,
        ISocketMessageChannel channel)
    {
        if (!IsApolloMessage(after)) return Task.CompletedTask;
        if (channel is not SocketTextChannel textChannel) return Task.CompletedTask;
        if (!IsEventsChannel(textChannel)) return Task.CompletedTask;

        _ = ProcessApolloMessageAsync(after, textChannel.Guild, isUpdate: true);
        return Task.CompletedTask;
    }

    private Task OnMessageDeleted(
        Cacheable<IMessage, ulong> message,
        Cacheable<IMessageChannel, ulong> _channel)
    {
        _ = HandleMessageDeletedAsync(message.Id);
        return Task.CompletedTask;
    }

    // ─── Core Processing ─────────────────────────────────────────────

    private async Task ProcessApolloMessageAsync(
        SocketMessage message, SocketGuild guild, bool isUpdate)
    {
        var embed = message.Embeds.FirstOrDefault();
        if (embed is null) return;

        // Serialize per-message processing. See _messageLocks for full
        // reasoning. Acquire BEFORE the diagnostic dump and parse so any
        // concurrent invocation for the same message waits here, not
        // halfway through.
        var gate = _messageLocks.GetOrAdd(message.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            // ── Diagnostic embed dump ─────────────────────────────────────
            // Logs a sanitized snapshot of the Apollo embed before parsing, so
            // we can see exactly what format Apollo posts when investigating
            // parser bugs (e.g. the 2026-04-25 Helldivers end-time bug where
            // Apollo's dashboard showed 7 AM – 9 PM but the parser produced
            // 12:00–14:00 UTC, suggesting only the start time was captured and
            // a 2-hour duration default was applied).
            //
            // We project the embed to a dictionary first because IEmbed isn't
            // directly serializable. The fields list preserves order so we can
            // see which field positions Apollo uses for time/organizer/etc.
            try
            {
                var diag = new
                {
                    Title       = embed.Title,
                    Description = embed.Description,
                    AuthorName  = embed.Author?.Name,
                    FooterText  = embed.Footer?.Text,
                    Fields      = embed.Fields.Select(f => new
                    {
                        f.Name,
                        f.Value,
                        f.Inline
                    }).ToList(),
                };

                _logger.LogInformation(
                    "Apollo embed (messageId={MessageId}, isUpdate={IsUpdate}): {EmbedJson}",
                    message.Id,
                    isUpdate,
                    Newtonsoft.Json.JsonConvert.SerializeObject(diag));
            }
            catch (Exception ex)
            {
                // Don't let logging blow up the actual processing path.
                _logger.LogWarning(ex, "Failed to serialize Apollo embed for diagnostics");
            }

            var parsed = ApolloEmbedParser.Parse(embed);
            if (parsed is null)
            {
                _logger.LogDebug(
                    "Could not parse Apollo embed from message {MessageId} — skipping", message.Id);
                return;
            }

            _logger.LogInformation(
                "Apollo event detected: '{Title}' {Start}–{End} UTC (messageId={MessageId}, update={IsUpdate})",
                parsed.Title, parsed.StartUtc, parsed.EndUtc, message.Id, isUpdate);

            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

                var existing = await db.CalendarEvents
                    .FirstOrDefaultAsync(c => c.DiscordMessageId == message.Id);

                // ── UPDATE path ───────────────────────────────────────────
                if (isUpdate && existing is not null)
                {
                    var timeChanged =
                        Math.Abs((existing.StartUtc - parsed.StartUtc).TotalMinutes) >= 1 ||
                        Math.Abs((existing.EndUtc   - parsed.EndUtc  ).TotalMinutes) >= 1;

                    // Only run the overlap check when the time actually changed —
                    // title/description edits can't create a new conflict.
                    if (timeChanged)
                    {
                        // Exclude the event itself; it still occupies its old slot in
                        // the calendar until we update it, so it would always self-overlap.
                        var overlaps = (await _calendarService.GetOverlappingEventsAsync(
                                parsed.StartUtc, parsed.EndUtc))
                            .Where(e => e.Id != existing.CalendarEventId)
                            .ToList();

                        if (overlaps.Count > 0)
                        {
                            _logger.LogWarning(
                                "Apollo event update '{Title}' overlaps {Count} existing calendar event(s)",
                                parsed.Title, overlaps.Count);

                            if (parsed.OrganizerId.HasValue)
                            {
                                var organizer = guild.GetUser(parsed.OrganizerId.Value);
                                if (organizer is not null)
                                    await SendOverlapDmAsync(organizer, parsed, overlaps, isReschedule: true);
                            }
                        }
                    }

                    await _calendarService.UpdateEventAsync(
                        existing.CalendarEventId,
                        parsed.Title,
                        parsed.StartUtc,
                        parsed.EndUtc,
                        description: parsed.Description ?? "");

                    existing.Title       = parsed.Title;
                    existing.StartUtc    = parsed.StartUtc;
                    existing.EndUtc      = parsed.EndUtc;
                    existing.Description = parsed.Description ?? "";
                    await db.SaveChangesAsync();

                    _logger.LogInformation(
                        "Updated calendar event for Apollo message {MessageId}", message.Id);
                    return;
                }

                // Skip duplicate creates (idempotency on restart).
                //
                // With the per-message lock above, a true concurrent-create race
                // can no longer reach this point. This branch still serves a
                // legitimate path: backfill / restart re-entry where a row
                // already exists for a non-update invocation. The
                // isUpdate=true && existing=null case explicitly does NOT take
                // this branch — it falls through to CREATE, which is correct
                // (see comment on the CREATE path).
                if (existing is not null) return;

                // ── CREATE path ───────────────────────────────────────────
                //
                // Reaches here when:
                //   • isUpdate=false, existing=null (normal new event)
                //   • isUpdate=true,  existing=null (Apollo edit arrived for
                //     a message we never recorded — bot was offline when the
                //     original was posted, or the gateway dropped the original
                //     MessageReceived event). Treating as create is correct;
                //     the alternative would be silently dropping the event.
                var createOverlaps = await _calendarService.GetOverlappingEventsAsync(
                    parsed.StartUtc, parsed.EndUtc);

                if (createOverlaps.Count > 0)
                {
                    _logger.LogWarning(
                        "Apollo event '{Title}' overlaps {Count} existing calendar event(s)",
                        parsed.Title, createOverlaps.Count);

                    if (parsed.OrganizerId.HasValue)
                    {
                        var organizer = guild.GetUser(parsed.OrganizerId.Value);
                        if (organizer is not null)
                            await SendOverlapDmAsync(organizer, parsed, createOverlaps);
                    }
                }

                var calEvent = await _calendarService.CreateEventAsync(
                    title:       parsed.Title,
                    startUtc:    parsed.StartUtc,
                    endUtc:      parsed.EndUtc,
                    description: parsed.Description ?? "",
                    creatorName: parsed.OrganizerName ?? "",
                    source:      "Clan");

                db.CalendarEvents.Add(new CalendarEvent
                {
                    GuildId          = guild.Id,
                    DiscordMessageId = message.Id,
                    CalendarEventId  = calEvent.Id,
                    Title            = parsed.Title,
                    StartUtc         = parsed.StartUtc,
                    EndUtc           = parsed.EndUtc,
                    Description      = parsed.Description ?? "",
                    Source           = "Clan",
                    CreatedAt        = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing Apollo event from message {MessageId}", message.Id);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Distinguishes pre-event (real cancellation) vs post-event (channel
    /// cleanup) Apollo deletions and acts on the former, ignores the latter.
    ///
    /// ── Why the distinction matters ──
    /// The 189th's policy is to remove an Apollo event's #events post once
    /// the event has finished, to keep the channel tidy. Earlier versions
    /// of this handler treated every Apollo message deletion as a request
    /// to remove the calendar entry — which silently destroyed the
    /// historical record of every event that had ever run, since each one
    /// got cleaned up shortly after EndUtc. The 2026-05-02 reconciliation
    /// pass surfaced this by deleting the few stragglers that had escaped
    /// earlier passes; investigating *why* there were stragglers revealed
    /// the policy mismatch.
    ///
    /// ── New behaviour ──
    /// • Deletion BEFORE EndUtc: organizer is pulling the event back
    ///   (wrong time, scheduling conflict, no longer happening). Treat as
    ///   cancellation: remove the Google Calendar entry and the
    ///   CalendarEvent row so they don't linger as ghosts. There can't be
    ///   any EventAttendance rows pointing at the row yet, because the
    ///   snapshot service only writes those after EndUtc.
    /// • Deletion AT/AFTER EndUtc: routine channel cleanup. Preserve the
    ///   Google Calendar entry as historical record and preserve the
    ///   CalendarEvent row so EventAttendance.CalendarEventId references
    ///   stay traceable for audits.
    ///
    /// ── Why the EndUtc cutoff is the right boundary ──
    /// EventAttendanceSnapshotService runs ~5 minutes after EndUtc. Any
    /// pre-event cancellation happens before any snapshot, so the "delete
    /// the row" branch never has to worry about orphaning attendance
    /// rows. Any post-event cleanup happens at or after EndUtc, when
    /// snapshots have either run or are imminent — preserving the row
    /// keeps the FK reference intact for those snapshots' lifetime.
    /// </summary>
    private async Task HandleMessageDeletedAsync(ulong messageId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = await db.CalendarEvents
                .FirstOrDefaultAsync(c => c.DiscordMessageId == messageId);

            if (record is null) return;

            // Owning-ClanEvent guard (mirrors ApolloReconciliationService's
            // candidate filter). The in-house event system (EventPublisher)
            // writes CalendarEvents with the SAME Source="Clan" value and posts
            // its RSVP message to GetEventPostChannelId() — which falls back to
            // the Apollo EventsTextChannelId when EventPostChannelId is unset.
            // That means an in-house event's message can share this channel and
            // match here purely by DiscordMessageId. When such a message is
            // deleted — e.g. as collateral of an Apollo /sort repost sweep, or a
            // manual delete — this handler would otherwise drop the Google
            // Calendar entry and the hub row, orphaning the ClanEvent + outbox
            // and silently removing the event from the calendar with no rebind.
            // A CalendarEvent owned by a ClanEvent is managed exclusively by
            // ClanGuard's own create/edit/cancel/archive lifecycle and must
            // never be deleted by Apollo message-deletion handling. Apollo-
            // sourced events never have an owning ClanEvent, so this is a no-op
            // for genuine Apollo deletions.
            if (await db.ClanEvents.AnyAsync(e => e.CalendarEventId == record.Id))
            {
                _logger.LogInformation(
                    "Apollo message {MessageId} deleted, but CalendarEvent Id={Id} '{Title}' is owned by an in-house ClanEvent; leaving it to ClanGuard's own lifecycle (no calendar delete)",
                    messageId, record.Id, record.Title);
                return;
            }

            // Post-event deletion → routine channel cleanup. Leave the
            // calendar entry and DB row alone; both are historical record.
            if (record.EndUtc <= DateTime.UtcNow)
            {
                _logger.LogDebug(
                    "Apollo message {MessageId} deleted after event end; preserving calendar event '{Title}' (EndUtc={End:yyyy-MM-dd HH:mm} UTC) as historical record",
                    messageId, record.Title, record.EndUtc);
                return;
            }

            // Pre-event deletion → real cancellation. Remove both the
            // Google Calendar entry and the DB row.
            await _calendarService.DeleteEventAsync(record.CalendarEventId);
            db.CalendarEvents.Remove(record);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Pre-event Apollo deletion: removed calendar event '{Title}' (messageId={MessageId}, EndUtc={End:yyyy-MM-dd HH:mm} UTC was in the future)",
                record.Title, messageId, record.EndUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error handling deletion of Apollo message {MessageId}", messageId);
        }
    }

    // ─── Overlap DM ──────────────────────────────────────────────────

    private async Task SendOverlapDmAsync(
        SocketGuildUser organizer,
        ApolloEmbedParser.ParsedApolloEvent newEvent,
        List<Google.Apis.Calendar.v3.Data.Event> overlaps,
        bool isReschedule = false)
    {
        try
        {
            var unixStart = new DateTimeOffset(newEvent.StartUtc).ToUnixTimeSeconds();

            var overlapLines = overlaps.Select(e =>
            {
                var eStart = GoogleCalendarService.ParseEventTime(e.Start);
                var timeStr = eStart.HasValue
                    ? $"<t:{new DateTimeOffset(eStart.Value).ToUnixTimeSeconds()}:F>"
                    : "unknown time";
                return $"• **{e.Summary}** — {timeStr}";
            });

            var action = isReschedule ? "rescheduled" : "posted";
            var outcome = isReschedule
                ? "The calendar has been updated to the new time."
                : "The event has been added to the calendar.";

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Schedule Conflict Detected")
                .WithColor(Color.Orange)
                .WithDescription(
                    $"Hey {organizer.Mention}! The event you just {action} — " +
                    $"**{newEvent.Title}** (<t:{unixStart}:F>) — " +
                    $"overlaps with the following existing event(s):\n\n" +
                    string.Join("\n", overlapLines) +
                    $"\n\n{outcome} " +
                    "Please reach out to the other organizer(s) to coordinate or reschedule.")
                .WithFooter("ClanGuard Bot • Calendar Conflict Alert")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            var dm = await organizer.CreateDMChannelAsync();
            await dm.SendMessageAsync(embed: embed);

            _logger.LogInformation(
                "Sent overlap DM to {Username} for event '{Title}'",
                organizer.Username, newEvent.Title);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not send overlap DM to {Username}", organizer.Username);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    private bool IsApolloMessage(SocketMessage message)
    {
        if (!message.Author.IsBot) return false;
        return message.Author.Username.Contains(
            _config.ApolloBotName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks by channel ID first (rename-proof), falls back to name comparison.
    /// </summary>
    private bool IsEventsChannel(SocketTextChannel channel) =>
        (_config.EventsTextChannelId != 0 && channel.Id == _config.EventsTextChannelId) ||
        channel.Name.Equals(_config.EventsTextChannelName, StringComparison.OrdinalIgnoreCase);
}