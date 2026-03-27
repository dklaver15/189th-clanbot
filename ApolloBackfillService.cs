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
/// parses any Apollo bot embeds it finds, and adds them to Google Calendar.
///
/// Already-synced messages (tracked in the CalendarEvents table by DiscordMessageId)
/// are skipped so the service is safe to run on every restart — it will only ever
/// process new-to-us messages.
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

        foreach (var guild in _client.Guilds)
        {
            var eventsChannel = guild.TextChannels.FirstOrDefault(c =>
                c.Id == _config.EventsTextChannelId);

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
        // Load all message IDs we've already synced so we can skip them cheaply
        HashSet<ulong> alreadySynced;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            alreadySynced = (await db.CalendarEvents
                .Where(c => c.GuildId == guild.Id && c.DiscordMessageId != 0)
                .Select(c => c.DiscordMessageId)
                .ToListAsync(ct))
                .ToHashSet();
        }

        var synced  = 0;
        var skipped = 0;
        var errors  = 0;

        var messages = ((ITextChannel)channel).GetMessagesAsync(limit: 100).Flatten();

        await foreach (var message in messages.WithCancellation(ct))
        {
            if (!message.Author.IsBot) continue;
            if (!message.Author.Username.Contains(
                    _config.ApolloBotName, StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Embeds.Count == 0) continue;

            if (alreadySynced.Contains(message.Id))
            {
                skipped++;
                continue;
            }

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

            try
            {
                var overlaps = await _calendarService.GetOverlappingEventsAsync(
                    parsed.StartUtc, parsed.EndUtc);

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

                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

                db.CalendarEvents.Add(new CalendarEvent
                {
                    GuildId          = guild.Id,
                    DiscordMessageId = message.Id,
                    CalendarEventId  = calEvent.Id,
                    Title            = parsed.Title,
                    StartUtc         = parsed.StartUtc,
                    EndUtc           = parsed.EndUtc,
                    Source           = "Clan",
                    CreatedAt        = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);

                alreadySynced.Add(message.Id);

                _logger.LogInformation(
                    "Apollo backfill: added '{Title}' ({Start} UTC) from message {MessageId}",
                    parsed.Title, parsed.StartUtc, message.Id);

                synced++;

                // Small delay between Calendar API calls to stay within quota
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
            "Apollo backfill #{Channel}: {Synced} added, {Skipped} skipped, {Errors} error(s)",
            channel.Name, synced, skipped, errors);
    }
}