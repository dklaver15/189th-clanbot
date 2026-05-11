using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// One-shot startup service that scrolls back through the configured events text channel,
/// parses any Apollo bot embeds it finds, and adds or updates them in Google Calendar.
///
/// Three outcomes for each Apollo message found:
///   • NEW   — not in DB yet → created in Calendar and DB.
///   • DRIFT — already in DB but title, time, or description has changed while the bot was
///             offline → calendar event updated and DB record refreshed.
///   • SKIP  — already in DB and nothing has changed → no-op.
///
/// Past events (ended more than 1 hour ago) are intentionally skipped.
/// </summary>
public class ApolloBackfillService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<ApolloBackfillService> _logger;
    private readonly BotConfig _config;

    public ApolloBackfillService(
        IServiceProvider services,
        DiscordSocketClient client,
        GoogleCalendarService calendarService,
        ILogger<ApolloBackfillService> logger,
        IOptions<BotConfig> config)
    {
        _services        = services;
        _client          = client;
        _calendarService = calendarService;
        _logger          = logger;
        _config          = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for Discord to be fully ready
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // Extra buffer so the forward-listening ApolloEventHandler is registered first
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // ── Phase 3 cutover ───────────────────────────────────────────────
        // When the new pipeline is active, backfill is structurally unsafe:
        // it walks channel history and writes CalendarEvents + GCal directly,
        // racing with ApolloMessageParserWorker writing CalendarEvents and
        // CalendarOutboxWorker pushing to GCal. The race can produce orphan
        // GCal events when backfill's CREATE path beats the parser worker's
        // outbox enqueue to the GCal API, then hits the unique index on
        // CalendarEvents and silently rolls back the DB write.
        //
        // ApolloMessageCaptureHandler covers the forward path (every gateway-
        // delivered Apollo message becomes an ApolloMessageLog row). The case
        // backfill exists for — "bot was offline, missed messages in the
        // channel" — is handled going forward by Phase 4's three-way
        // reconciler. For Phase 3, skipping the channel-history walk is the
        // safe trade.
        if (_config.UseNewApolloPipeline)
        {
            _logger.LogInformation(
                "Apollo backfill skipped: UseNewApolloPipeline=true. " +
                "Forward path is handled by ApolloMessageCaptureHandler + ApolloMessageParserWorker + CalendarOutboxWorker; " +
                "offline-gap recovery will be handled by Phase 4's three-way reconciler.");
            return;
        }

        foreach (var guild in _client.Guilds)
        {
            var eventsChannel = guild.TextChannels.FirstOrDefault(c =>
                (_config.EventsTextChannelId != 0 && c.Id == _config.EventsTextChannelId) ||
                c.Name.Equals(_config.EventsTextChannelName, StringComparison.OrdinalIgnoreCase));

            if (eventsChannel is null)
            {
                _logger.LogWarning(
                    "Apollo backfill: events channel '{Channel}' not found in {Guild} — skipping",
                    _config.EventsTextChannelName, guild.Name);
                continue;
            }

            _logger.LogInformation(
                "Apollo backfill starting for #{Channel} in {Guild}",
                eventsChannel.Name, guild.Name);

            await BackfillChannelAsync(eventsChannel, guild, stoppingToken);
        }

        _logger.LogInformation("Apollo backfill complete.");
    }

    private async Task BackfillChannelAsync(
        SocketTextChannel channel, SocketGuild guild, CancellationToken ct)
    {
        // Load all events we've already synced with their full details so we can
        // detect drift (title, time, or description changed while the bot was offline).
        // Use GroupBy to handle duplicate DiscordMessageIds gracefully — keep the most recent record.
        Dictionary<ulong, CalendarEvent> syncedByMessageId;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var events = await db.CalendarEvents
                .Where(c => c.GuildId == guild.Id && c.DiscordMessageId != 0)
                .ToListAsync(ct);

            syncedByMessageId = events
                .GroupBy(c => c.DiscordMessageId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(c => c.Id).First());

            // Clean up any duplicates found
            var duplicates = events
                .GroupBy(c => c.DiscordMessageId)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.OrderByDescending(c => c.Id).Skip(1))
                .ToList();

            if (duplicates.Count > 0)
            {
                _logger.LogWarning(
                    "Apollo backfill: removing {Count} duplicate CalendarEvent record(s)",
                    duplicates.Count);
                db.CalendarEvents.RemoveRange(duplicates);
                await db.SaveChangesAsync(ct);
            }
        }

        var synced  = 0;
        var updated = 0;
        var skipped = 0;
        var errors  = 0;

        var messages = ((ITextChannel)channel).GetMessagesAsync(limit: 100).Flatten();

        await foreach (var message in messages.WithCancellation(ct))
        {
            if (!message.Author.IsBot) continue;
            if (!message.Author.Username.Contains(
                    _config.ApolloBotName, StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Embeds.Count == 0) continue;

            var embed  = message.Embeds.First();
            var parsed = ApolloEmbedParser.Parse(embed);

            if (parsed is null)
            {
                _logger.LogDebug(
                    "Apollo backfill: could not parse embed in message {MessageId} — skipping",
                    message.Id);
                skipped++;
                continue;
            }

            // Skip events that have already fully ended
            if (parsed.EndUtc < DateTime.UtcNow.AddHours(-1))
            {
                _logger.LogDebug(
                    "Apollo backfill: skipping past event '{Title}' (ended {End} UTC)",
                    parsed.Title, parsed.EndUtc);
                skipped++;
                continue;
            }

            // ── UPDATE path: event already in DB, check for drift ─────────
            if (syncedByMessageId.TryGetValue(message.Id, out var existing))
            {
                var titleChanged = !string.Equals(existing.Title, parsed.Title, StringComparison.Ordinal);
                var startChanged = Math.Abs((existing.StartUtc - parsed.StartUtc).TotalMinutes) >= 1;
                var endChanged   = Math.Abs((existing.EndUtc   - parsed.EndUtc  ).TotalMinutes) >= 1;
                var descChanged  = !string.Equals(
                    existing.Description, parsed.Description ?? "", StringComparison.Ordinal);

                if (!titleChanged && !startChanged && !endChanged && !descChanged)
                {
                    skipped++;
                    continue;
                }

                _logger.LogInformation(
                    "Apollo backfill: drift detected for '{Title}' (message {MessageId}) — " +
                    "title={TitleChanged} start={StartChanged} end={EndChanged} desc={DescChanged}",
                    parsed.Title, message.Id, titleChanged, startChanged, endChanged, descChanged);

                try
                {
                    await _calendarService.UpdateEventAsync(
                        existing.CalendarEventId,
                        parsed.Title,
                        parsed.StartUtc,
                        parsed.EndUtc,
                        parsed.Description ?? "");

                    using var scope = _services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                    var record = await db.CalendarEvents
                        .FirstOrDefaultAsync(c => c.DiscordMessageId == message.Id, ct);

                    if (record is not null)
                    {
                        record.Title       = parsed.Title;
                        record.StartUtc    = parsed.StartUtc;
                        record.EndUtc      = parsed.EndUtc;
                        record.Description = parsed.Description ?? "";
                        await db.SaveChangesAsync(ct);
                    }

                    _logger.LogInformation(
                        "Apollo backfill: updated '{Title}' ({Start} UTC) from message {MessageId}",
                        parsed.Title, parsed.StartUtc, message.Id);

                    updated++;
                    await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Apollo backfill: error updating message {MessageId} ('{Title}')",
                        message.Id, parsed.Title);
                    errors++;
                }

                continue;
            }

            // ── CREATE path: new event ────────────────────────────────────
            try
            {
                // Check for an existing calendar event with the same title and time
                // to prevent duplicates from crash-loop restarts
                var overlaps = await _calendarService.GetOverlappingEventsAsync(
                    parsed.StartUtc, parsed.EndUtc);

                var isDuplicate = overlaps.Any(e =>
                    string.Equals(e.Summary, parsed.Title, StringComparison.OrdinalIgnoreCase));

                if (isDuplicate)
                {
                    _logger.LogInformation(
                        "Apollo backfill: skipping '{Title}' — duplicate already exists on calendar",
                        parsed.Title);

                    // Still record it in DB so we don't check again next restart
                    using var dupScope = _services.CreateScope();
                    var dupDb = dupScope.ServiceProvider.GetRequiredService<BotDbContext>();

                    var existingOverlap = overlaps.First(e =>
                        string.Equals(e.Summary, parsed.Title, StringComparison.OrdinalIgnoreCase));

                    dupDb.CalendarEvents.Add(new CalendarEvent
                    {
                        GuildId          = guild.Id,
                        DiscordMessageId = message.Id,
                        CalendarEventId  = existingOverlap.Id,
                        Title            = parsed.Title,
                        StartUtc         = parsed.StartUtc,
                        EndUtc           = parsed.EndUtc,
                        Description      = parsed.Description ?? "",
                        Source           = "Clan",
                        CreatedAt        = DateTime.UtcNow
                    });
                    await dupDb.SaveChangesAsync(ct);

                    skipped++;
                    continue;
                }

                if (overlaps.Count > 0)
                {
                    _logger.LogWarning(
                        "Apollo backfill: '{Title}' overlaps {Count} existing calendar event(s) — adding anyway",
                        parsed.Title, overlaps.Count);
                }

                var calEvent = await _calendarService.CreateEventAsync(
                    title:       parsed.Title,
                    startUtc:    parsed.StartUtc,
                    endUtc:      parsed.EndUtc,
                    description: parsed.Description ?? "",
                    creatorName: parsed.OrganizerName ?? "",
                    source:      "Clan");

                using var createScope = _services.CreateScope();
                var createDb = createScope.ServiceProvider.GetRequiredService<BotDbContext>();

                createDb.CalendarEvents.Add(new CalendarEvent
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
                await createDb.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "Apollo backfill: added '{Title}' ({Start} UTC) from message {MessageId}",
                    parsed.Title, parsed.StartUtc, message.Id);

                synced++;
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Apollo backfill: error syncing message {MessageId} ('{Title}')",
                    message.Id, parsed.Title);
                errors++;
            }
        }

        _logger.LogInformation(
            "Apollo backfill #{Channel}: {Synced} added, {Updated} updated, {Skipped} skipped, {Errors} error(s)",
            channel.Name, synced, updated, skipped, errors);
    }
}