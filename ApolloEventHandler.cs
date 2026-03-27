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
/// Monitors the configured events text channel for posts from the Apollo bot.
///
/// For each Apollo event embed, this handler:
///   1. Parses the event title, start/end time, and organizer via ApolloEmbedParser.
///   2. Checks Google Calendar for any overlapping events.
///   3. If there is an overlap, DMs the organizer with the conflict details.
///   4. Adds the event to Google Calendar (tagged "Clan", teal color).
///   5. Persists a CalendarEvent DB record so edits/deletes stay in sync.
///
/// MessageUpdated — updates the calendar event if Apollo edits the post.
/// MessageDeleted — removes the calendar event if the Apollo post is deleted.
/// </summary>
public class ApolloEventHandler
{
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
                await _calendarService.UpdateEventAsync(
                    existing.CalendarEventId, parsed.Title,
                    parsed.StartUtc, parsed.EndUtc);

                existing.Title    = parsed.Title;
                existing.StartUtc = parsed.StartUtc;
                existing.EndUtc   = parsed.EndUtc;
                await db.SaveChangesAsync();

                _logger.LogInformation(
                    "Updated calendar event for Apollo message {MessageId}", message.Id);
                return;
            }

            // Skip duplicate creates (idempotency on restart / rare race)
            if (existing is not null) return;

            // ── CREATE path ───────────────────────────────────────────
            var overlaps = await _calendarService.GetOverlappingEventsAsync(
                parsed.StartUtc, parsed.EndUtc);

            if (overlaps.Count > 0)
            {
                _logger.LogWarning(
                    "Apollo event '{Title}' overlaps {Count} existing calendar event(s)",
                    parsed.Title, overlaps.Count);

                if (parsed.OrganizerId.HasValue)
                {
                    var organizer = guild.GetUser(parsed.OrganizerId.Value);
                    if (organizer is not null)
                        await SendOverlapDmAsync(organizer, parsed, overlaps);
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

    private async Task HandleMessageDeletedAsync(ulong messageId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var record = await db.CalendarEvents
                .FirstOrDefaultAsync(c => c.DiscordMessageId == messageId);

            if (record is null) return;

            await _calendarService.DeleteEventAsync(record.CalendarEventId);
            db.CalendarEvents.Remove(record);
            await db.SaveChangesAsync();

            _logger.LogInformation(
                "Calendar event '{Title}' removed because Apollo message {MessageId} was deleted",
                record.Title, messageId);
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
        List<Google.Apis.Calendar.v3.Data.Event> overlaps)
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

            var embed = new EmbedBuilder()
                .WithTitle("⚠️ Schedule Conflict Detected")
                .WithColor(Color.Orange)
                .WithDescription(
                    $"Hey {organizer.Mention}! The event you just posted — " +
                    $"**{newEvent.Title}** (<t:{unixStart}:F>) — " +
                    $"overlaps with the following existing event(s):\n\n" +
                    string.Join("\n", overlapLines) +
                    "\n\nThe event has been added to the calendar. " +
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

    private bool IsEventsChannel(SocketTextChannel channel) =>
        channel.Name.Equals(
            _config.EventsTextChannelName, StringComparison.OrdinalIgnoreCase);
}