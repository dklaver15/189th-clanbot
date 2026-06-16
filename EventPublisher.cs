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

        // A series whose first occurrence is beyond the horizon would otherwise
        // post NOTHING on creation, leaving the organizer with an invisible event
        // until the scheduler's rolling window catches up (potentially weeks
        // later) — and the wizard would still claim it was "posted to #events".
        // Always materialize at least the first upcoming occurrence so the event
        // is visible immediately; the scheduler tops up the rest as they enter the
        // horizon. (Scan to a far future and take 1 to find that first occurrence.)
        if (occurrences.Count == 0)
        {
            occurrences = ClanEventRecurrence
                .Occurrences(series, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddYears(50), 1)
                .ToList();

            if (occurrences.Count == 0)
                _logger.LogWarning(
                    "Series {SeriesId} '{Title}' produced no occurrences at all (check UntilUtc / MaxOccurrences)",
                    series.Id, series.Title);
            else
                _logger.LogInformation(
                    "Series {SeriesId} '{Title}' first occurrence {Start:o} is beyond the {Horizon}d horizon; posting it now so the event is visible immediately",
                    series.Id, series.Title, occurrences[0].StartUtc, _config.EventRecurrenceHorizonDays);
        }

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
    /// Create a Custom (specific-dates) series — an explicit, irregular set of
    /// nights — and post one occurrence per date. Unlike PublishSeriesAsync there
    /// is no rule or horizon: every listed date is materialized now, and the
    /// scheduler never adds more (ClanEventRecurrence yields nothing for Custom).
    /// </summary>
    public async Task PublishSpecificDatesAsync(EventDraft d)
    {
        // First date (the When step) + the extras, de-duped and ordered.
        var starts = new List<DateTime> { d.StartUtc };
        starts.AddRange(d.SpecificDatesUtc);
        starts = starts.Distinct().OrderBy(x => x).ToList();

        if (starts.Count <= 1)
        {
            // Only one date after all — nothing irregular, so it's a one-off.
            await PublishOneOffAsync(d);
            return;
        }

        var tz         = ResolveZone(d.TimeZoneId);
        var firstLocal = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(starts[0], tz), DateTimeKind.Unspecified);
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
                Frequency       = ClanEventFrequency.Custom,
                TimeZoneId      = string.IsNullOrWhiteSpace(d.TimeZoneId) ? tz.Id : d.TimeZoneId,
                FirstStartLocal = firstLocal,
                DurationMinutes = durMinutes,
                ChannelId       = _config.GetEventPostChannelId(),
                UntilUtc        = null,
                MaxOccurrences  = null,
                MaxParticipants = d.MaxParticipants,
                Active          = true,
                CreatedAt       = DateTime.UtcNow,
                ImageBytes      = d.ImageBytes,
                ImageFileName   = d.ImageFileName,
            };
            db.ClanEventSeries.Add(series);
            await db.SaveChangesAsync(); // materialize series.Id
        }

        var duration = TimeSpan.FromMinutes(durMinutes);
        foreach (var startUtc in starts)
            await CreateOccurrenceAsync(d.GuildId, series.Id,
                d.Title, d.Description, d.OrganizerId, d.OrganizerName, startUtc, startUtc + duration,
                attachImageBytes: series.ImageBytes, imageFileName: series.ImageFileName,
                maxParticipants: series.MaxParticipants);

        _logger.LogInformation(
            "Created custom-date series {SeriesId} '{Title}'; materialized {Count} occurrence(s)",
            series.Id, series.Title, starts.Count);
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

        // 2) Persist the CalendarEvent hub row, the ClanEvent, and the outbox row
        //    ATOMICALLY. Without the transaction, a failure after the first save
        //    would leave an orphaned CalendarEvent (no owning ClanEvent, no outbox
        //    row) that nothing syncs or cleans up. On any failure we roll back and
        //    delete the message we just posted, so a botched create leaves neither
        //    a half-written DB nor a dangling backing-less #events post.
        ClanEvent clanEvent;
        int calEventId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();

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
            calEventId = calEvent.Id;

            clanEvent = new ClanEvent
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
                // No separate host at creation — the creator is the effective host.
                // A later "Set Host" enqueues an Update that overwrites this line.
                HostName      = organizerName,
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

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            // Rollback is automatic on dispose. Remove the now-orphaned post so we
            // don't leave a backing-less embed in #events, then let the caller
            // (wizard / scheduler) surface the failure.
            _logger.LogError(ex,
                "Persisting event '{Title}' failed after posting msg {MessageId}; rolled back, removing the orphaned post",
                title, messageId);
            try { await posted.DeleteAsync(); }
            catch (Exception delEx)
            {
                _logger.LogWarning(delEx,
                    "Couldn't delete orphaned post {MessageId} after a failed create", messageId);
            }
            throw;
        }

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
            title, startUtc, endUtc, clanEvent.Id, calEventId, messageId);
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

    public async Task FillHorizonAsync(ClanEventSeries series)
    {
        var horizonEnd  = DateTime.UtcNow.AddDays(_config.EventRecurrenceHorizonDays);
        var occurrences = ClanEventRecurrence
            .Occurrences(series, DateTime.UtcNow.AddMinutes(-1), horizonEnd, _config.EventRecurrenceMaxBackfill)
            .ToList();

        foreach (var (startUtc, endUtc) in occurrences)
            await CreateOccurrenceAsync(series.GuildId, series.Id,
                series.Title, series.Description, series.OrganizerId, series.OrganizerName,
                startUtc, endUtc,
                attachImageBytes: series.ImageBytes, imageFileName: series.ImageFileName,
                maxParticipants: series.MaxParticipants);
    }

    public async Task MaterializeDatesAsync(ClanEventSeries series, IEnumerable<DateTime> startsUtc)
    {
        var duration = TimeSpan.FromMinutes(series.DurationMinutes);
        foreach (var startUtc in startsUtc.Distinct().OrderBy(x => x))
            await CreateOccurrenceAsync(series.GuildId, series.Id,
                series.Title, series.Description, series.OrganizerId, series.OrganizerName,
                startUtc, startUtc + duration,
                attachImageBytes: series.ImageBytes, imageFileName: series.ImageFileName,
                maxParticipants: series.MaxParticipants);
    }
}
