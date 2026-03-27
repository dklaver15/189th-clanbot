using System.Text.RegularExpressions;
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
/// The service runs once after Discord is ready, then stops. It does NOT re-run on
/// subsequent restarts unless there are new un-synced messages to process.
/// </summary>
public partial class ApolloBackfillService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly GoogleCalendarService _calendarService;
    private readonly ILogger<ApolloBackfillService> _logger;
    private readonly BotConfig _config;

    // Matches <t:UNIX> or <t:UNIX:F> etc.
    [GeneratedRegex(@"<t:(\d+)(?::[a-zA-Z])?>", RegexOptions.Compiled)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"<@!?(\d+)>", RegexOptions.Compiled)]
    private static partial Regex MentionRegex();

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
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        // Extra buffer so the forward-listening ApolloEventHandler is registered first
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        foreach (var guild in _client.Guilds)
        {
            var eventsChannel = guild.TextChannels.FirstOrDefault(c =>
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

        // GetMessagesAsync returns newest-first; we process them all but only add future/recent ones
        var messages = channel.GetMessagesAsync(limit: int.MaxValue).Flatten();

        await foreach (var message in messages.WithCancellation(ct))
        {
            // Only care about Apollo bot posts with embeds
            if (!message.Author.IsBot) continue;
            if (!message.Author.Username.Contains(
                    _config.ApolloBotName, StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Embeds.Count == 0) continue;

            // Skip already-synced messages
            if (alreadySynced.Contains(message.Id))
            {
                skipped++;
                continue;
            }

            var embed = message.Embeds.First();
            var parsed = ParseApolloEmbed(embed);

            if (parsed is null)
            {
                _logger.LogDebug(
                    "Apollo backfill: could not parse embed in message {MessageId} — skipping",
                    message.Id);
                skipped++;
                continue;
            }

            // Skip events that have already fully ended — no point adding stale events
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
                // Check for overlaps (informational only — we still add the event)
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

                alreadySynced.Add(message.Id); // prevent duplicate if iterator returns it again

                _logger.LogInformation(
                    "Apollo backfill: added '{Title}' ({Start} UTC) from message {MessageId}",
                    parsed.Title, parsed.StartUtc, message.Id);

                synced++;

                // Small delay between Calendar API calls to stay well within quota
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

    // ─── Embed Parsing (mirrors ApolloEventHandler) ──────────────────

    private ParsedApolloEvent? ParseApolloEmbed(IEmbed embed)
    {
        var title = !string.IsNullOrWhiteSpace(embed.Title)
            ? embed.Title
            : embed.Author?.Name;

        if (string.IsNullOrWhiteSpace(title)) return null;

        var allText = BuildEmbedText(embed);

        var timestamps = TimestampRegex()
            .Matches(allText)
            .Select(m => DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups[1].Value)).UtcDateTime)
            .OrderBy(t => t)
            .ToList();

        if (timestamps.Count == 0) return null;

        var startUtc = timestamps[0];

        DateTime endUtc;
        if (timestamps.Count >= 2 && timestamps[1] > startUtc.AddMinutes(15))
            endUtc = timestamps[1];
        else
            endUtc = startUtc.AddMinutes(ParseDurationMinutes(embed));

        ulong? organizerId = null;
        var organizerFieldValue = embed.Fields
            .FirstOrDefault(f =>
                f.Name.Contains("organizer",  StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("host",       StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("created by", StringComparison.OrdinalIgnoreCase))
            .Value;

        var mentionSource = !string.IsNullOrEmpty(organizerFieldValue)
            ? organizerFieldValue
            : embed.Description ?? "";

        var mentionMatch = MentionRegex().Match(mentionSource);
        if (mentionMatch.Success && ulong.TryParse(mentionMatch.Groups[1].Value, out var uid))
            organizerId = uid;

        return new ParsedApolloEvent
        {
            Title         = title.Trim(),
            StartUtc      = startUtc,
            EndUtc        = endUtc,
            OrganizerId   = organizerId,
            OrganizerName = embed.Author?.Name,
            Description   = embed.Description
        };
    }

    private static string BuildEmbedText(IEmbed embed)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(embed.Description)) parts.Add(embed.Description);
        foreach (var field in embed.Fields)
        {
            parts.Add(field.Name);
            parts.Add(field.Value);
        }
        if (!string.IsNullOrEmpty(embed.Footer?.Text)) parts.Add(embed.Footer.Value.Text);
        return string.Join("\n", parts);
    }

    private static int ParseDurationMinutes(IEmbed embed)
    {
        var durationText = embed.Fields
            .FirstOrDefault(f =>
                f.Name.Contains("duration", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("length",   StringComparison.OrdinalIgnoreCase))
            .Value;

        if (string.IsNullOrEmpty(durationText)) return 120;

        var hoursMatch = Regex.Match(durationText, @"(\d+)\s*h(?:ours?)?", RegexOptions.IgnoreCase);
        var minsMatch  = Regex.Match(durationText, @"(\d+)\s*m(?:in(?:utes?)?)?", RegexOptions.IgnoreCase);

        var hours   = hoursMatch.Success ? int.Parse(hoursMatch.Groups[1].Value) : 0;
        var minutes = minsMatch.Success  ? int.Parse(minsMatch.Groups[1].Value)  : 0;

        var total = hours * 60 + minutes;
        return total > 0 ? total : 120;
    }

    private sealed record ParsedApolloEvent
    {
        public string    Title         { get; init; } = "";
        public DateTime  StartUtc      { get; init; }
        public DateTime  EndUtc        { get; init; }
        public ulong?    OrganizerId   { get; init; }
        public string?   OrganizerName { get; init; }
        public string?   Description   { get; init; }
    }
}
