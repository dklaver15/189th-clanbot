using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Posts reminders for upcoming events at the configured lead times
/// (EventReminderLeadMinutes, default 60 and 15 minutes before start). Each
/// reminder is an embed; the accompanying ping line @-mentions the organizer
/// plus every member whose RSVP is in EventReminderPingStatuses (default Going,
/// Maybe, and Waitlisted), and still posts when no one else matches.
///
/// ── Restart-safe ──
/// Fired lead values are recorded on ClanEvent.RemindersSentCsv and saved before
/// posting, so a `docker restart` or a failed save never double-fires a reminder
/// (at worst one post is skipped). If the bot was down
/// across several lead times, only the most urgent (smallest) due lead fires;
/// the larger, now-stale ones are marked sent without posting.
/// </summary>
public sealed class EventReminderService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<EventReminderService> _logger;
    private readonly PalworldApiService _palworld;
    private readonly FrmApiService _frm;

    public EventReminderService(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<EventReminderService> logger,
        PalworldApiService palworld,
        FrmApiService frm)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _logger   = logger;
        _palworld = palworld;
        _frm      = frm;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation(
            "EventReminderService started; polling every {Seconds}s (enabled={Enabled}, leads={Leads})",
            (int)PollInterval.TotalSeconds, _config.EventReminderEnabled, _config.EventReminderLeadMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.EventReminderEnabled)
                    await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EventReminderService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var leads = ParseLeadMinutes(_config.EventReminderLeadMinutes);
        if (leads.Count == 0) return;

        var pingStatuses = ParsePingStatuses(_config.EventReminderPingStatuses);
        var maxLead = leads.Max();

        var now    = DateTime.UtcNow;
        var window = now.AddMinutes(maxLead);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Only events whose soonest reminder could be due right now. MessageId != 0
        // excludes unposted recurring-series occurrences (we only remind for the
        // one that's actually shown in #events).
        var candidates = await db.ClanEvents
            .Where(e => e.Status == ClanEventStatus.Scheduled
                     && e.MessageId != 0
                     && e.StartUtc > now
                     && e.StartUtc <= window)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        foreach (var ev in candidates)
        {
            if (ct.IsCancellationRequested) break;

            var fired = ParseCsvInts(ev.RemindersSentCsv);
            var due = leads.Where(L => !fired.Contains(L) && now >= ev.StartUtc.AddMinutes(-L)).ToList();
            if (due.Count == 0) continue;

            // Fire only the most urgent due lead; suppress any stale larger ones
            // (relevant only after downtime).
            var fire = due.Min();
            var mentions = await BuildMentionsAsync(db, ev, pingStatuses, ct);

            // Mark the leads sent and save before posting, so a shutdown or failed
            // save can't post the reminder again next tick.
            foreach (var L in due) fired.Add(L);
            ev.RemindersSentCsv = string.Join(",", fired.OrderByDescending(x => x));
            await db.SaveChangesAsync(CancellationToken.None);

            await PostReminderAsync(ev, mentions);
            await db.SaveChangesAsync(CancellationToken.None); // LastReminderMessageId
            await AnnounceInGameAsync(ev, fire, ct);
        }
    }

    private async Task<string> BuildMentionsAsync(
        BotDbContext db, ClanEvent ev, HashSet<EventRsvpStatus> statuses, CancellationToken ct)
    {
        // Always ping the organizer; add anyone whose RSVP matches the configured
        // statuses (deduped — the organizer may also have RSVP'd).
        var ids = new HashSet<ulong> { ev.OrganizerId };

        if (statuses.Count > 0)
        {
            var rsvpIds = await db.EventRsvps
                .Where(r => r.ClanEventId == ev.Id && statuses.Contains(r.Status))
                .Select(r => r.UserId)
                .ToListAsync(ct);
            foreach (var id in rsvpIds) ids.Add(id);
        }

        return string.Join(" ", ids.Select(id => $"<@{id}>"));
    }

    private async Task PostReminderAsync(ClanEvent ev, string mentions)
    {
        var channelId = _config.EventReminderChannelId != 0
            ? _config.EventReminderChannelId
            : _config.EventsTextChannelId;

        if (_client.GetChannel(channelId) is not IMessageChannel channel)
        {
            _logger.LogError(
                "Reminder channel {Channel} is not reachable; skipping reminder for '{Title}'",
                channelId, ev.Title);
            return; // lead still marked sent by caller to avoid a retry loop
        }

        // Remember the prior reminder (if any) so we can delete it once the new one
        // is up, keeping just the latest reminder in the channel. The id lives on
        // the event row (not in memory) so the delete still fires when the bot
        // restarts between two lead times.
        var prevMsgId = ev.LastReminderMessageId;
        var hadPrev = _config.EventReminderReplacePrevious && prevMsgId != 0;

        var hasImage = ev.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(ev.ImageFileName);

        // Deep link back to the event's RSVP post in #events so a click on the
        // reminder title jumps straight to it. Same jump-URL shape used elsewhere
        // (UpcomingEventsBoardService, EventWaitlist). Only attach when the ids are
        // present; an embed URL must be a real link, so a 0 id would be invalid.
        var jump = ev.GuildId != 0 && ev.ChannelId != 0 && ev.MessageId != 0
            ? $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{ev.MessageId}"
            : null;

        var eb = new EmbedBuilder()
            .WithColor(new Color(0x5865F2))
            .WithTitle($"⏰ {ev.Title}")
            .WithDescription(
                $"Starts {EventTimeParser.Stamp(ev.StartUtc, 'R')}\n{EventTimeParser.Stamp(ev.StartUtc, 'F')}");
        if (jump is not null)
            eb.WithUrl(jump);
        if (hasImage)
            eb.WithImageUrl($"attachment://{ev.ImageFileName}");
        var embed = eb.Build();

        // Mentions must live in the message *content* to actually notify — pings
        // inside an embed don't fire. AllowedMentions limits this to users.
        var content = string.IsNullOrWhiteSpace(mentions) ? null : mentions;
        var allowed = new AllowedMentions { AllowedTypes = AllowedMentionTypes.Users };

        // Send with a single retry. A transient Discord blip shouldn't cost a
        // member their reminder, but we cap at one retry so a permanent failure
        // (e.g. the channel was deleted) can't spin — and the caller marks the
        // lead sent regardless, so this never loops across polls.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                IUserMessage posted;
                if (hasImage)
                {
                    using var fa = new FileAttachment(new MemoryStream(ev.ImageBytes!), ev.ImageFileName);
                    posted = await channel.SendFileAsync(fa, text: content, embed: embed, allowedMentions: allowed);
                }
                else
                {
                    posted = await channel.SendMessageAsync(text: content, embed: embed, allowedMentions: allowed);
                }

                // Record the new reminder's id on the event; the caller saves the
                // tracked entity so this survives a restart.
                ev.LastReminderMessageId = posted.Id;

                // New reminder is up — remove the previous one to declutter.
                if (hadPrev)
                {
                    try { await channel.DeleteMessageAsync(prevMsgId); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Couldn't delete previous reminder {Msg} for '{Title}'", prevMsgId, ev.Title); }
                }
                return; // delivered
            }
            catch (Exception ex) when (attempt == 1)
            {
                _logger.LogDebug(ex, "Reminder for '{Title}' failed (attempt 1); retrying in 2s", ev.Title);
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post reminder for '{Title}' after retry", ev.Title);
            }
        }
    }

    /// <summary>
    /// Mirrors the Discord reminder as an in-game broadcast on EVERY game server
    /// the clan runs, so members mid-session don't miss an event just because
    /// they aren't looking at Discord. Fires on the same lead times as the
    /// Discord post.
    ///
    /// ── Why this is safe to bolt on here ──
    /// Deliberately best-effort and non-fatal: both game clients swallow every
    /// failure and return false, and each announce is additionally wrapped so
    /// nothing about a game server being down can interfere with the Discord
    /// reminder — which has already been posted by the time we get here. The
    /// lead is marked sent by the caller regardless, so a missed announce is
    /// never retried into a loop. The two servers are independent: one failing
    /// cannot stop the other.
    ///
    /// Both directions are Discord→game only. Neither game exposes a chat relay
    /// back out.
    ///
    /// SyncWithHandlers: BotConfig.PalworldEventAnnounceEnabled,
    /// BotConfig.SatisfactoryEventAnnounceEnabled.
    /// </summary>
    private async Task AnnounceInGameAsync(ClanEvent ev, int leadMinutes, CancellationToken ct)
    {
        // Announcements are plain text — no markdown, no timestamps. State the
        // lead in words rather than pasting a Discord <t:…> stamp, which would
        // render as literal garbage in game.
        var lead = leadMinutes >= 60 && leadMinutes % 60 == 0
            ? $"{leadMinutes / 60}h"
            : $"{leadMinutes} min";

        await AnnouncePalworldAsync(ev, lead, leadMinutes, ct);
        await AnnounceSatisfactoryAsync(ev, lead, leadMinutes, ct);
    }

    /// <summary>
    /// Satisfactory, via FRM's sendChatMessage.
    ///
    /// <para>Sent as an A.D.A. message rather than from a named sender, so it
    /// appears in the voice the game already uses for announcements and reads as
    /// part of the world rather than as a bot in the chat.</para>
    ///
    /// <para><b>Needs FrmAuthToken.</b> Every FRM read works without it; writes
    /// do not. If it's unset this quietly does nothing, which is the one failure
    /// mode here that looks like the feature simply not working — hence the
    /// explicit debug line in the client.</para>
    ///
    /// <para>Announces EVERY event, not only Satisfactory ones, exactly as the
    /// Palworld path does. There is no game field on ClanEvent to filter by, and
    /// with four members the cross-game noise is not worth guessing at titles.</para>
    ///
    /// SyncWithHandlers: BotConfig.SatisfactoryEventAnnounceEnabled.
    /// </summary>
    private async Task AnnounceSatisfactoryAsync(ClanEvent ev, string lead, int leadMinutes, CancellationToken ct)
    {
        if (!_config.SatisfactoryEnabled
            || !_config.FrmEnabled
            || !_config.SatisfactoryEventAnnounceEnabled
            || !_frm.IsConfigured)
            return;

        try
        {
            var ok = await _frm.SendChatMessageAsync($"[189th] Event starting in {lead}: {ev.Title}", ct: ct);

            if (ok)
                _logger.LogInformation(
                    "Announced '{Title}' in-game on the Satisfactory server ({Lead} min lead)", ev.Title, leadMinutes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A game server hiccup must never affect event reminders.
            _logger.LogDebug(ex, "In-game Satisfactory announce failed for '{Title}'", ev.Title);
        }
    }

    /// <summary>
    /// Palworld, via the REST API's /announce — the only Discord→game channel
    /// it exposes.
    ///
    /// SyncWithHandlers: BotConfig.PalworldEventAnnounceEnabled.
    /// </summary>
    private async Task AnnouncePalworldAsync(ClanEvent ev, string lead, int leadMinutes, CancellationToken ct)
    {
        if (!_config.PalworldEnabled
            || !_config.PalworldEventAnnounceEnabled
            || !_palworld.IsConfigured)
            return;

        try
        {
            var ok = await _palworld.AnnounceAsync($"[189th] Event starting in {lead}: {ev.Title}", ct);

            if (ok)
                _logger.LogInformation(
                    "Announced '{Title}' in-game on the Palworld server ({Lead} min lead)", ev.Title, leadMinutes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A game server hiccup must never affect event reminders.
            _logger.LogDebug(ex, "In-game Palworld announce failed for '{Title}'", ev.Title);
        }
    }

    private static List<int> ParseLeadMinutes(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? n : -1)
            .Where(n => n > 0)
            .Distinct()
            .OrderByDescending(n => n)
            .ToList();

    private static HashSet<int> ParseCsvInts(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? n : -1)
            .Where(n => n > 0)
            .ToHashSet();

    private static HashSet<EventRsvpStatus> ParsePingStatuses(string csv)
    {
        var set = new HashSet<EventRsvpStatus>();
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "going":      set.Add(EventRsvpStatus.Going);      break;
                case "maybe":      set.Add(EventRsvpStatus.Maybe);      break;
                case "decline":    set.Add(EventRsvpStatus.Decline);    break;
                case "waitlisted": set.Add(EventRsvpStatus.Waitlisted); break;
            }
        }
        if (set.Count == 0) { set.Add(EventRsvpStatus.Going); set.Add(EventRsvpStatus.Maybe); set.Add(EventRsvpStatus.Waitlisted); } // sensible default
        return set;
    }
}
