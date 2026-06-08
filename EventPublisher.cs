using ClanGuardBot.Data;
using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace ClanGuardBot.Services;

/// <summary>
/// Turns a confirmed <see cref="EventDraft"/> into a live event: posts the RSVP
/// embed to #events, writes a <see cref="ClanEvent"/> plus the
/// <see cref="CalendarEvent"/> hub row (Source="Clan") and a CalendarOutbox
/// Create row, so Google Calendar sync and voice-based attendance credit work
/// exactly as they did for Apollo events. For series, creates a
/// <see cref="ClanEventSeries"/> and materializes the initial occurrences.
///
/// ── Per-occurrence sequence ──
/// Discord can't reorder messages and the unique index on ClanEvent.MessageId
/// forbids a placeholder, so each occurrence: (1) posts the embed (no buttons)
/// to get a real, unique message id; (2) writes CalendarEvent + ClanEvent +
/// outbox; (3) edits the message to attach the RSVP buttons, which need the
/// now-known ClanEvent.Id. The CalendarOutboxWorker drains the outbox → GCal,
/// stamping CalendarEvent.CalendarEventId on success.
/// </summary>
public sealed class EventPublisher : IEventPublisher
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<EventPublisher> _logger;

    public EventPublisher(
        DiscordSocketClient client,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<EventPublisher> logger)
    {
        _client   = client;
        _services = services;
        _config   = config.Value;
        _logger   = logger;
    }

    public Task PublishOneOffAsync(EventDraft d) =>
        CreateOccurrenceAsync(d.GuildId, seriesId: null,
            d.Title, d.Description, d.OrganizerId, d.OrganizerName, d.StartUtc, d.EndUtc,
            attachImageBytes: d.ImageBytes, imageFileName: d.ImageFileName,
            maxParticipants: d.MaxParticipants);

    public async Task PublishSeriesAsync(EventDraft d)
    {
        if (d.Frequency is not ClanEventFrequency freq)
        {
            // Defensive: a series draft with no frequency is really a one-off.
            await PublishOneOffAsync(d);
            return;
        }

        var tz         = ResolveZone(d.TimeZoneId);
        var firstLocal = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(d.StartUtc, tz), DateTimeKind.Unspecified);
        var durMinutes = (int)Math.Round((d.EndUtc - d.StartUtc).TotalMinutes);

        ClanEventSeries series;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            series = new ClanEventSeries
            {
                GuildId         = d.GuildId,
                Title           = d.Title,
                Description     = d.Description,
                OrganizerId     = d.OrganizerId,
                OrganizerName   = d.OrganizerName,
                Frequency       = freq,
                TimeZoneId      = string.IsNullOrWhiteSpace(d.TimeZoneId) ? tz.Id : d.TimeZoneId,
                FirstStartLocal = firstLocal,
                DurationMinutes = durMinutes,
                ChannelId       = _config.GetEventPostChannelId(),
                UntilUtc        = d.UntilUtc,
                MaxOccurrences  = d.MaxOccurrences,
                MaxParticipants = d.MaxParticipants,
                Active          = true,
                CreatedAt       = DateTime.UtcNow,
                ImageBytes      = d.ImageBytes,
                ImageFileName   = d.ImageFileName,
            };
            db.ClanEventSeries.Add(series);
            await db.SaveChangesAsync(); // materialize series.Id
        }

        // Materialize the initial batch of occurrences within the horizon,
        // capped by EventRecurrenceMaxBackfill so a daily series doesn't flood
        // #events on creation. The scheduler tops the window up over time.
        var horizonEnd = DateTime.UtcNow.AddDays(_config.EventRecurrenceHorizonDays);
        var occurrences = ClanEventRecurrence
            .Occurrences(series, DateTime.UtcNow.AddMinutes(-1), horizonEnd, _config.EventRecurrenceMaxBackfill)
            .ToList();

        if (occurrences.Count == 0)
            _logger.LogWarning("Series {SeriesId} '{Title}' produced no occurrences in the horizon", series.Id, series.Title);

        foreach (var (startUtc, endUtc) in occurrences)
            await CreateOccurrenceAsync(d.GuildId, series.Id,
                d.Title, d.Description, d.OrganizerId, d.OrganizerName, startUtc, endUtc,
                attachImageBytes: series.ImageBytes, imageFileName: series.ImageFileName,
                maxParticipants: series.MaxParticipants);

        _logger.LogInformation(
            "Created series {SeriesId} '{Title}' ({Freq}); materialized {Count} occurrence(s)",
            series.Id, series.Title, freq, occurrences.Count);
    }

    /// <summary>
    /// Idempotent creation of one occurrence. For series occurrences, skips if a
    /// ClanEvent already exists for (SeriesId, StartUtc) — the same guard the
    /// scheduler relies on.
    /// </summary>
    public async Task CreateOccurrenceAsync(
        ulong guildId, int? seriesId,
        string title, string description, ulong organizerId, string organizerName,
        DateTime startUtc, DateTime endUtc,
        byte[]? attachImageBytes = null, string? imageFileName = null,
        int? maxParticipants = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        if (seriesId is int sid &&
            await db.ClanEvents.AnyAsync(e => e.SeriesId == sid && e.StartUtc == startUtc))
        {
            _logger.LogDebug("Occurrence for series {SeriesId} at {Start:o} already exists; skipping", sid, startUtc);
            return;
        }

        var postChannelId = _config.GetEventPostChannelId();
        if (_client.GetChannel(postChannelId) is not IMessageChannel channel)
        {
            _logger.LogError("Event post channel {Channel} is not a reachable message channel; cannot post event '{Title}'",
                postChannelId, title);
            return;
        }

        // 1) Post the embed (no buttons) to obtain a real, unique message id.
        var preview = new ClanEvent
        {
            GuildId = guildId, SeriesId = seriesId, Title = title, Description = description,
            OrganizerId = organizerId, OrganizerName = organizerName,
            StartUtc = startUtc, EndUtc = endUtc, Status = ClanEventStatus.Scheduled,
            MaxParticipants = maxParticipants,
        };
        var previewEmbed = EventEmbedBuilder.BuildEmbed(preview, Array.Empty<EventRsvp>(), imageFileName);
        IUserMessage posted;
        if (attachImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(imageFileName))
        {
            using var fa = new FileAttachment(new MemoryStream(attachImageBytes), imageFileName);
            posted = await channel.SendFileAsync(fa, embed: previewEmbed);
        }
        else
        {
            posted = await channel.SendMessageAsync(embed: previewEmbed);
        }
        var messageId = posted.Id;

        // 2) Persist the CalendarEvent hub row, the ClanEvent, and the outbox row.
        var calEvent = new CalendarEvent
        {
            GuildId          = guildId,
            DiscordMessageId = messageId,   // unique, satisfies the filtered index
            CalendarEventId  = string.Empty, // stamped by CalendarOutboxWorker on GCal success
            Title            = title,
            StartUtc         = startUtc,
            EndUtc           = endUtc,
            Description      = description ?? string.Empty,
            Source           = "Clan",      // AttendanceCountingSources="Clan" → counts for promotion
            ContentHash      = string.Empty, // Clan events never go through /sort rebind
            CreatedAt        = DateTime.UtcNow,
        };
        db.CalendarEvents.Add(calEvent);
        await db.SaveChangesAsync(); // materialize calEvent.Id

        var clanEvent = new ClanEvent
        {
            GuildId          = guildId,
            SeriesId         = seriesId,
            CalendarEventId  = calEvent.Id,
            ChannelId        = channel.Id,
            MessageId        = messageId,
            Title            = title,
            Description      = description ?? string.Empty,
            OrganizerId      = organizerId,
            OrganizerName    = organizerName,
            StartUtc         = startUtc,
            EndUtc           = endUtc,
            Status           = ClanEventStatus.Scheduled,
            RemindersSentCsv = string.Empty,
            CreatedAt        = DateTime.UtcNow,
            ImageFileName    = imageFileName,
            ImageBytes       = seriesId == null ? attachImageBytes : null,
            MaxParticipants  = maxParticipants,
        };
        db.ClanEvents.Add(clanEvent);

        var payload = new CalendarOutboxPayload
        {
            Title         = title,
            StartUtc      = startUtc,
            EndUtc        = endUtc,
            Description   = description ?? string.Empty,
            OrganizerName = organizerName,
            OrganizerId   = organizerId,
            Source        = "Clan",
        };
        db.CalendarOutbox.Add(new CalendarOutbox
        {
            GuildId         = guildId,
            Operation       = CalendarOutboxOperation.Create,
            CalendarEventId = calEvent.Id,
            PayloadJson     = JsonConvert.SerializeObject(payload),
            NextAttemptAt   = DateTime.UtcNow,
            CreatedAt       = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(); // materialize clanEvent.Id

        // 3) Attach the RSVP buttons now that we have ClanEvent.Id.
        try
        {
            var locked = DateTime.UtcNow >= startUtc;
            var components = EventEmbedBuilder.BuildComponents(clanEvent.Id, _config.EventRsvpEnabled, locked);
            await posted.ModifyAsync(m => m.Components = components);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Event '{Title}' posted (msg {MessageId}) but attaching RSVP buttons failed", title, messageId);
        }

        _logger.LogInformation(
            "Posted event '{Title}' {Start:o}–{End:o} UTC (ClanEvent {ClanId}, CalendarEvent {CalId}, msg {MessageId}, GCal create queued)",
            title, startUtc, endUtc, clanEvent.Id, calEvent.Id, messageId);
    }

    private TimeZoneInfo ResolveZone(string? ianaId)
    {
        if (!string.IsNullOrWhiteSpace(ianaId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var tz) && tz is not null)
            return tz;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(_config.EventDefaultTimeZone, out var def) && def is not null)
            return def;
        return TimeZoneInfo.Utc;
    }
}
