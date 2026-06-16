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

        await PromoteNextAsync(series.Id); // post the first/next occurrence to #events

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

        await PromoteNextAsync(series.Id); // post the first/next occurrence to #events

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
        var channel = _client.GetChannel(postChannelId) as IMessageChannel;

        // Recurring-series occurrences are materialized WITHOUT a Discord post —
        // only the next-up occurrence is shown in #events (posted later by
        // PromoteNextAsync). The rest still get a CalendarEvent + GCal entry, so
        // the Google Calendar carries the full schedule. One-offs always post now.
        var postNow = seriesId is null;

        if (postNow && channel is null)
        {
            _logger.LogError("Event post channel {Channel} is not a reachable message channel; cannot post event '{Title}'",
                postChannelId, title);
            return;
        }

        // 1) For a one-off, post first to get a real, unique message id. Series
        //    occurrences start unposted (MessageId = 0; the filtered unique index
        //    on MessageId allows many zeros to coexist).
        ulong messageId = 0;
        IUserMessage? posted = null;
        if (postNow)
        {
            var preview = new ClanEvent
            {
                GuildId = guildId, SeriesId = seriesId, Title = title, Description = description,
                OrganizerId = organizerId, OrganizerName = organizerName,
                StartUtc = startUtc, EndUtc = endUtc, Status = ClanEventStatus.Scheduled,
                MaxParticipants = maxParticipants,
            };
            var previewEmbed = EventEmbedBuilder.BuildEmbed(preview, Array.Empty<EventRsvp>(), imageFileName);
            if (attachImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(imageFileName))
            {
                using var fa = new FileAttachment(new MemoryStream(attachImageBytes), imageFileName);
                posted = await channel!.SendFileAsync(fa, embed: previewEmbed);
            }
            else
            {
                posted = await channel!.SendMessageAsync(embed: previewEmbed);
            }
            messageId = posted.Id;
        }

        // 2) Persist the CalendarEvent hub row, the ClanEvent, and the outbox row
        //    ATOMICALLY. Without the transaction, a failure after the first save
        //    would leave an orphaned CalendarEvent (no owning ClanEvent, no outbox
        //    row) that nothing syncs or cleans up. On any failure we roll back and
        //    delete the message we just posted (if any).
        ClanEvent clanEvent;
        int calEventId;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync();

            var calEvent = new CalendarEvent
            {
                GuildId          = guildId,
                DiscordMessageId = messageId,   // 0 for an unposted series occurrence
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
                ChannelId        = postChannelId,
                MessageId        = messageId,   // 0 until the occurrence is posted
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
            _logger.LogError(ex,
                "Persisting event '{Title}' {Start:o} failed; rolled back{PostNote}",
                title, startUtc, posted is null ? "" : ", removing the orphaned post");
            if (posted is not null)
            {
                try { await posted.DeleteAsync(); }
                catch (Exception delEx) { _logger.LogWarning(delEx, "Couldn't delete orphaned post {MessageId}", messageId); }
            }
            throw;
        }

        // 3) Attach the RSVP buttons on the one-off post now that we have ClanEvent.Id.
        if (postNow && posted is not null)
        {
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
        }

        _logger.LogInformation(
            "Materialized event '{Title}' {Start:o}–{End:o} UTC (ClanEvent {ClanId}, CalendarEvent {CalId}, posted={Posted}, GCal create queued)",
            title, startUtc, endUtc, clanEvent.Id, calEventId, postNow);
    }

    /// <summary>
    /// Posts the soonest upcoming, not-yet-posted occurrence of a series to
    /// #events, so a recurring series always shows exactly its next occurrence.
    /// Idempotent: a no-op when the next occurrence is already posted, or there is
    /// none upcoming. Called after every materialization (creation, scheduler
    /// top-up, edit) and naturally promotes the following occurrence once the
    /// current one has passed and been archived.
    /// </summary>
    public async Task PromoteNextAsync(int seriesId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var upcoming = await db.ClanEvents
            .Where(e => e.SeriesId == seriesId
                     && e.Status == ClanEventStatus.Scheduled
                     && e.StartUtc > now)
            .OrderBy(e => e.StartUtc)
            .ToListAsync();

        if (upcoming.Count == 0) return;

        var postChannelId = _config.GetEventPostChannelId();
        if (_client.GetChannel(postChannelId) is not IMessageChannel channel)
        {
            _logger.LogWarning(
                "PromoteNext: event post channel {Channel} not reachable; will retry next tick", postChannelId);
            return;
        }

        // Post the soonest upcoming occurrence if it isn't already up.
        var next = upcoming[0];
        if (next.MessageId == 0)
            await PostOccurrenceAsync(db, channel, next);

        // Enforce "next only": un-post any OTHER still-posted upcoming occurrences
        // (existing series from before this feature, or extras left by an edit).
        // RSVP rows are kept, so they reappear when that occurrence later becomes
        // the next-up and is re-posted.
        foreach (var extra in upcoming.Skip(1).Where(e => e.MessageId != 0))
            await UnpostOccurrenceAsync(db, channel, extra);
    }

    /// <summary>
    /// Removes an occurrence's #events post and clears its MessageId so it no
    /// longer shows, without touching its RSVP rows or its CalendarEvent / Google
    /// Calendar entry. Reversible: PromoteNext re-posts it when it becomes next-up.
    /// </summary>
    private async Task UnpostOccurrenceAsync(BotDbContext db, IMessageChannel channel, ClanEvent ev)
    {
        var oldMsg = ev.MessageId;
        try { await channel.DeleteMessageAsync(ev.MessageId); }
        catch (Exception ex) { _logger.LogDebug(ex, "Un-post: couldn't delete message {Msg} for event {Id}", oldMsg, ev.Id); }

        ev.MessageId = 0;
        var cal = await db.CalendarEvents.FirstOrDefaultAsync(c => c.Id == ev.CalendarEventId);
        if (cal is not null) cal.DiscordMessageId = 0;
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Un-posted extra upcoming occurrence {Id} '{Title}' (msg {Msg}) — keeping only the next in #events",
            ev.Id, ev.Title, oldMsg);
    }

    /// <summary>
    /// Posts an existing, unposted (<see cref="ClanEvent.MessageId"/> = 0) occurrence:
    /// renders the embed from current state, sends it, and re-points the ClanEvent
    /// and its CalendarEvent at the new message, then attaches the RSVP buttons.
    /// </summary>
    private async Task PostOccurrenceAsync(BotDbContext db, IMessageChannel channel, ClanEvent ev)
    {
        var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
        var (imgBytes, imgName) = await EventImage.ResolveAsync(db, ev);
        var embed = EventEmbedBuilder.BuildEmbed(ev, rsvps, imgName);

        IUserMessage posted;
        if (imgBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(imgName))
        {
            using var fa = new FileAttachment(new MemoryStream(imgBytes), imgName);
            posted = await channel.SendFileAsync(fa, embed: embed);
        }
        else
        {
            posted = await channel.SendMessageAsync(embed: embed);
        }

        ev.MessageId = posted.Id;
        var cal = await db.CalendarEvents.FirstOrDefaultAsync(c => c.Id == ev.CalendarEventId);
        if (cal is not null) cal.DiscordMessageId = posted.Id;
        await db.SaveChangesAsync();

        try
        {
            var locked = DateTime.UtcNow >= ev.StartUtc;
            await posted.ModifyAsync(m =>
                m.Components = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Posted next occurrence {Id} but attaching RSVP buttons failed", ev.Id);
        }

        _logger.LogInformation(
            "Posted next occurrence of series {SeriesId}: ClanEvent {Id} '{Title}' (msg {Msg})",
            ev.SeriesId, ev.Id, ev.Title, posted.Id);
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

        // Show only the next occurrence in #events: post the soonest upcoming one
        // if it isn't already up (this is also what promotes the following
        // occurrence once the current passes and is archived).
        await PromoteNextAsync(series.Id);
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

        await PromoteNextAsync(series.Id);
    }
}
