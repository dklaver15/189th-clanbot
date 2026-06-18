using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Globalization;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Edit and cancel flows for the in-house event system, reached via
/// /event edit and /event cancel (dispatched here by EventCommandHandler).
///
/// Both start with a shared ephemeral picker of the caller's upcoming events
/// (all events for EventCommandMinRank+; otherwise only their own). For a
/// recurring event, the caller is then asked "this occurrence vs whole series"
/// — mirroring the cancel/edit symmetry.
///
/// Calendar side-effects reuse the existing CalendarOutbox: edits enqueue
/// Update (the worker reads CalendarEvent.CalendarEventId to patch GCal);
/// cancels enqueue Delete carrying the GoogleEventId in the payload and remove
/// the CalendarEvent row, which both stops attendance snapshotting and makes
/// any still-pending Create skip itself.
///
/// ── Edit scope ──
/// One-off: title/when/duration/description. Single occurrence of a series:
/// title/duration/description (no time move — that would orphan the cadence
/// slot and the scheduler would regenerate a duplicate). Whole series:
/// title/description, propagated to all future scheduled occurrences.
/// </summary>
public sealed class EventManagementHandler
{
    private const string Prefix = "evtmgmt:";

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly EventTimeParser _time;
    private readonly EventChannelSorter _sorter;
    private readonly ILogger<EventManagementHandler> _logger;

    // /event image supplies the file at command time but applies it after the
    // user picks an event (a later interaction), so the bytes are stashed here
    // per user between the two steps.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan PendingImageTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<ulong, PendingImage> _pendingImages = new();

    private sealed record PendingImage(byte[]? Bytes, string? FileName, bool Clear, DateTime At);

    // ─── DM edit sessions (Apollo-style numbered-menu editing) ─────────────
    // Clicking "Edit" opens a DM wizard instead of a modal: a numbered field
    // menu, edits staged into the session, applied to the live post only when
    // the user types "done". Keyed by user id; expired after EditIdleTimeout.
    private static readonly TimeSpan EditIdleTimeout = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<ulong, EditSession> _editSessions = new();
    private System.Threading.Timer? _editIdleSweep;

    private enum EditStep { Menu, Title, When, Duration, Description, Image, MaxParticipants, Action, AddStatus, AddWho, RemoveWho, ClearConfirm, Recurrence, RecurrenceUntil, RecurrenceDates }

    private sealed class EditSession
    {
        public ulong UserId;
        public IDMChannel Dm = null!;
        public int ClanEventId;          // the clicked occurrence
        public string Scope = "single";  // "single" | "occ" | "series"
        public int? SeriesId;
        public TimeZoneInfo Tz = TimeZoneInfo.Utc;
        public EditStep Step = EditStep.Menu;
        public DateTime LastActivityAt;

        // Working copy, pre-loaded from the event.
        public string Title = string.Empty;
        public DateTime StartUtc;
        public DateTime EndUtc;
        public string Description = string.Empty;
        public int? MaxParticipants;

        // Image is staged separately (we don't preload bytes): only touched if
        // the user actually changes it during this session.
        public bool HasImage;        // did the event have a banner when we started
        public bool ImageChanged;    // did the user stage an image change
        public bool ImageCleared;    // ...and was it a removal
        public byte[]? ImageBytes;
        public string? ImageFileName;

        // Attendee-management scratch (Add a response / Remove a response).
        public EventRsvpStatus PendingAddStatus;
        public List<(ulong UserId, EventRsvpStatus Status)> RemoveCandidates = new();

        // ── Repeat-schedule editing (staged like the other fields; applied on "done") ──
        // CurrentFrequency is the schedule as it stands now (null = one-off / single
        // scope). New* hold the staged target: NewFrequency null = "does not repeat",
        // Custom = an explicit date set in NewDatesUtc. ScheduleChanged gates whether
        // ApplyEditsAsync performs any recurrence regeneration at all.
        public ClanEventFrequency? CurrentFrequency;
        public string ScheduleNowLabel = "Does not repeat";
        public bool ScheduleChanged;
        public ClanEventFrequency? NewFrequency;
        public DateTime? NewUntilUtc;
        public int? NewMaxOccurrences;
        public List<DateTime> NewDatesUtc = new();
    }

    public EventManagementHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        EventTimeParser time,
        EventChannelSorter sorter,
        ILogger<EventManagementHandler> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _time     = time;
        _sorter   = sorter;
        _logger   = logger;

        // Proactively drop (and notify) edit sessions idle past EditIdleTimeout.
        _editIdleSweep = new System.Threading.Timer(
            _ => _ = SweepEditSessionsAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(DiscordSocketClient client)
    {
        client.SelectMenuExecuted += OnSelectAsync;
        client.ButtonExecuted     += OnButtonAsync;
        client.MessageReceived    += OnEditDmAsync;
        client.UserLeft           += OnUserLeftAsync;
        client.GuildMemberUpdated += OnGuildMemberUpdatedAsync;
    }

    /// <summary>
    /// When a member's display name (nickname / global name) changes, re-render any
    /// upcoming posted events where they appear — in a roster or as the host — so the
    /// snapshotted names stay current. Names are resolved at render time (we render
    /// profile links, not live mentions), so without this a rename wouldn't show
    /// until the post was rebuilt for some other reason. Filtered to actual
    /// display-name changes (not the far more frequent role/avatar updates) and to
    /// the events that actually show the member, to keep it cheap.
    /// </summary>
    private async Task OnGuildMemberUpdatedAsync(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
    {
        // Only act on a real display-name change. If "before" isn't cached we can't
        // tell, so skip (AlwaysDownloadUsers keeps it warm, so genuine renames are
        // caught; a rare miss self-heals on the next render).
        if (before.Value is not { } prev || prev.DisplayName == after.DisplayName) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var now = DateTime.UtcNow;
            var rsvpEventIds = await db.EventRsvps
                .Where(r => r.UserId == after.Id)
                .Select(r => r.ClanEventId)
                .ToListAsync();

            var events = await db.ClanEvents
                .Where(e => e.GuildId == after.Guild.Id
                         && e.Status == ClanEventStatus.Scheduled
                         && e.EndUtc > now
                         && e.MessageId != 0   // only posted occurrences have a message to update
                         && (rsvpEventIds.Contains(e.Id)
                             || e.HostId == after.Id
                             || (e.HostId == null && e.OrganizerId == after.Id)))
                .ToListAsync();

            if (events.Count == 0) return;

            foreach (var ev in events)
                await UpdatePostAsync(db, ev);

            _logger.LogInformation(
                "Re-rendered {Count} event(s) after {User} changed display name '{Old}' → '{New}'",
                events.Count, after.Id, prev.DisplayName, after.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh events after display-name change for {User}", after.Id);
        }
    }

    /// <summary>
    /// When a member leaves the guild, pull their RSVPs from every still-upcoming
    /// scheduled event, re-render those posts, and promote waitlisters into any
    /// "Going" slot they freed. Past/ended events keep their roster as history, and
    /// unposted recurring occurrences just lose the stale RSVP row (no post to edit).
    /// </summary>
    private async Task OnUserLeftAsync(SocketGuild guild, SocketUser user)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var userRsvps = await db.EventRsvps.Where(r => r.UserId == user.Id).ToListAsync();
            if (userRsvps.Count == 0) return;

            var now = DateTime.UtcNow;
            var eventIds = userRsvps.Select(r => r.ClanEventId).Distinct().ToList();
            var events = await db.ClanEvents
                .Where(e => eventIds.Contains(e.Id)
                         && e.GuildId == guild.Id
                         && e.Status == ClanEventStatus.Scheduled
                         && e.EndUtc > now)
                .ToListAsync();
            if (events.Count == 0) return;

            var affected = events.Select(e => e.Id).ToHashSet();
            db.EventRsvps.RemoveRange(userRsvps.Where(r => affected.Contains(r.ClanEventId)));
            await db.SaveChangesAsync();

            foreach (var ev in events)
            {
                var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
                var promoted = EventWaitlist.Rebalance(rsvps, ev.MaxParticipants);
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

                await UpdatePostAsync(db, ev);
                foreach (var uid in promoted) await EventWaitlist.NotifyPromotedAsync(_client, ev, uid);
            }

            _logger.LogInformation(
                "Cleared RSVPs for departed member {User} from {Count} upcoming event(s)", user.Id, events.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear RSVPs for departed member {User}", user.Id);
        }
    }

    // ─── Entry points (called by EventCommandHandler) ──────────────────────

    public async Task StartCancelAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        await ShowPickerAsync(command, "cancel");
    }

    public async Task StartEditAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        await ShowPickerAsync(command, "edit");
    }

    public async Task StartImageAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var sub        = command.Data.Options.FirstOrDefault();
        var attachment = sub?.Options?.FirstOrDefault(o => o.Name == "image")?.Value as IAttachment;
        var url         = sub?.Options?.FirstOrDefault(o => o.Name == "url")?.Value as string;
        var clear      = sub?.Options?.FirstOrDefault(o => o.Name == "clear")?.Value as bool? ?? false;

        if (!clear && attachment is null && string.IsNullOrWhiteSpace(url))
        {
            await command.FollowupAsync("Attach an image, paste a GIF/image `url:`, or pass `clear:true` to remove the current one.", ephemeral: true);
            return;
        }

        PendingImage pending;
        if (clear)
        {
            pending = new PendingImage(null, null, Clear: true, DateTime.UtcNow);
        }
        else if (attachment is not null)
        {
            var looksImage = (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                          || EventImage.IsAllowedExtension(attachment.Filename);
            if (!looksImage)
            {
                await command.FollowupAsync("That doesn't look like an image (PNG/JPG/GIF/WebP).", ephemeral: true);
                return;
            }
            if (attachment.Size > EventImage.MaxBytes)
            {
                await command.FollowupAsync($"That image is too large. Max is {EventImage.MaxBytes / (1024 * 1024)} MB.", ephemeral: true);
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await Http.GetByteArrayAsync(attachment.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to download event image for {User}", command.User.Id);
                await command.FollowupAsync("Couldn't download that image. Try again.", ephemeral: true);
                return;
            }
            if (bytes.Length > EventImage.MaxBytes)
            {
                await command.FollowupAsync($"That image is too large. Max is {EventImage.MaxBytes / (1024 * 1024)} MB.", ephemeral: true);
                return;
            }

            var imgName = EventImage.Sanitize(attachment.Filename);
            pending = new PendingImage(EventImage.Downscale(bytes, imgName), imgName, Clear: false, DateTime.UtcNow);
        }
        else
        {
            var res = await EventImageFetcher.FromUrlAsync(url!);
            if (!res.Ok)
            {
                await command.FollowupAsync(res.Error ?? "Couldn't use that link.", ephemeral: true);
                return;
            }
            pending = new PendingImage(EventImage.Downscale(res.Bytes!, res.FileName), res.FileName, Clear: false, DateTime.UtcNow);
        }

        _pendingImages[command.User.Id] = pending;
        await ShowPickerAsync(command, "image");
    }

    public async Task StartSortAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);
        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }
        if (command.User is not SocketGuildUser gu || !HasEventPermission(gu))
        {
            await command.FollowupAsync($"❌ Sorting events is restricted to **{_config.EventCommandMinRank} and above**.", ephemeral: true);
            return;
        }

        var count = await _sorter.SortAsync(command.GuildId.Value);

        // The re-post changed every event's MessageId; refresh the pinned board so
        // its jump links point at the new posts. Non-fatal — a board hiccup must
        // never fail the sort the officer just ran.
        try { await _services.GetRequiredService<UpcomingEventsBoardService>().RefreshAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Board refresh after /event sort failed"); }

        await command.FollowupAsync(
            count <= 1 ? "Nothing to sort — there's at most one upcoming event." : $"✅ Re-posted {count} events in chronological order.",
            ephemeral: true);
    }

    private async Task ShowPickerAsync(SocketSlashCommand command, string action)
    {
        // Caller has already deferred (ephemeral).
        var display = action switch
        {
            "image" => "set an image for",
            _       => action,
        };

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        var canManageAll = command.User is SocketGuildUser gu && HasEventPermission(gu);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var query = db.ClanEvents
            .Where(e => e.GuildId == command.GuildId.Value
                     && e.Status == ClanEventStatus.Scheduled
                     && e.StartUtc > now);
        if (!canManageAll)
            query = query.Where(e => e.OrganizerId == command.User.Id);

        var events = await query.OrderBy(e => e.StartUtc).Take(25).ToListAsync();

        if (events.Count == 0)
        {
            await command.FollowupAsync($"You have no upcoming events to {display}.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}{action}:pick")
            .WithPlaceholder($"Choose an event to {display}")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var ev in events)
        {
            var label = Truncate(ev.Title, 100);
            var desc  = Truncate($"{(ev.SeriesId.HasValue ? "🔁 " : "")}starts {FormatRelative(ev.StartUtc - now)}", 100);
            menu.AddOption(label, ev.Id.ToString(), desc);
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await command.FollowupAsync($"Which event would you like to {display}?", components: components, ephemeral: true);
    }

    // ─── Select menu ───────────────────────────────────────────────────────

    private async Task OnSelectAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;

        // "Set Host" user-select (evtmgmt:hostpick:<id>) — values are user ids,
        // not event ids, so handle it before the event-id picker parsing below.
        if (id.StartsWith($"{Prefix}hostpick:", StringComparison.Ordinal))
        {
            await ApplyHostAsync(component, id);
            return;
        }

        if (id is not ($"{Prefix}cancel:pick" or $"{Prefix}edit:pick" or $"{Prefix}image:pick"))
            return;

        if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var clanEventId))
        {
            await component.UpdateAsync(m => { m.Content = "Something went wrong reading that selection."; m.Components = Empty(); });
            return;
        }

        var isCancel = id == $"{Prefix}cancel:pick";
        var isImage  = id == $"{Prefix}image:pick";

        ClanEvent? ev;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        }

        if (ev is null || ev.Status == ClanEventStatus.Cancelled)
        {
            await component.UpdateAsync(m => { m.Content = "That event is no longer available."; m.Components = Empty(); });
            return;
        }
        if (!await CallerMayManageAsync(component.User, ev.OrganizerId))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to manage that event."; m.Components = Empty(); });
            return;
        }

        // /event image applies to the one-off event or the whole series — no
        // this-occurrence/whole-series prompt (per-occurrence banners aren't a thing).
        if (isImage)
        {
            await ApplyImageAsync(component, ev);
            return;
        }

        // Recurring → ask this-occurrence vs whole-series first.
        if (ev.SeriesId.HasValue)
        {
            var verb = isCancel ? "cancel" : "edit";
            var buttons = new ComponentBuilder()
                .WithButton("This occurrence", $"{Prefix}{(isCancel ? "cxl" : "editpick")}:occ:{ev.Id}", ButtonStyle.Primary)
                .WithButton("Whole series",    $"{Prefix}{(isCancel ? "cxl" : "editpick")}:series:{ev.Id}", ButtonStyle.Danger)
                .WithButton("Never mind",      $"{Prefix}abort", ButtonStyle.Secondary);
            await component.UpdateAsync(m =>
            {
                m.Content    = $"**{ev.Title}** is a recurring event. {char.ToUpper(verb[0]) + verb[1..]} just this occurrence, or the whole series?";
                m.Components = buttons.Build();
            });
            return;
        }

        // One-off.
        if (isCancel)
        {
            await DoCancelAsync(clanEventId, wholeSeries: false);
            await component.UpdateAsync(m => { m.Content = $"✅ Cancelled **{ev.Title}**."; m.Components = Empty(); });
        }
        else
        {
            var ok = await BeginEditDmAsync(component.User, ev, "single");
            await component.UpdateAsync(m =>
            {
                m.Content = ok
                    ? "📬 I've sent you a DM to edit this event."
                    : "I couldn't DM you — check that DMs from server members are enabled.";
                m.Components = Empty();
            });
        }
    }

    // ─── Image apply (/event image) ────────────────────────────────────────

    private async Task ApplyImageAsync(SocketMessageComponent component, ClanEvent ev)
    {
        if (!_pendingImages.TryRemove(component.User.Id, out var pending)
            || DateTime.UtcNow - pending.At > PendingImageTtl)
        {
            await component.UpdateAsync(m => { m.Content = "That image request expired — run `/event image` again."; m.Components = Empty(); });
            return;
        }

        // Ack first — applying to a whole series re-renders every future
        // occurrence's post, which can exceed Discord's 3-second window.
        try { await component.UpdateAsync(m => { m.Content = "⏳ Updating image…"; m.Components = Empty(); }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Image ack failed for event {Id}", ev.Id); }

        var (scopeLabel, rerendered) = await ApplyImageToEventAsync(ev.Id, pending);
        if (scopeLabel is null) { await FinalizeImageAsync(component, "That event no longer exists."); return; }

        var what = pending.Clear ? "Removed the image" : "Updated the image";
        var tail = scopeLabel == "series" ? $" ({rerendered} post{(rerendered == 1 ? "" : "s")} updated)." : ".";
        await FinalizeImageAsync(component, $"✅ {what} on this {scopeLabel}{tail}");
    }

    /// <summary>
    /// Core image apply shared by the <c>/event image</c> picker and the on-post
    /// "Image" button: writes bytes/filename to the one-off event (or the series
    /// plus every future occurrence) and re-renders each post in place. Returns
    /// the scope label ("event"/"series") and how many posts were re-rendered, or
    /// (null, 0) if the event/series no longer exists.
    /// </summary>
    private async Task<(string? scopeLabel, int rerendered)> ApplyImageToEventAsync(int clanEventId, PendingImage pending)
    {
        var rerendered = 0;
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        if (ev is null) return (null, 0);

        if (ev.SeriesId is int sid)
        {
            var series = await db.ClanEventSeries.FirstOrDefaultAsync(s => s.Id == sid);
            if (series is null) return (null, 0);

            series.ImageBytes    = pending.Clear ? null : pending.Bytes;
            series.ImageFileName = pending.Clear ? null : pending.FileName;

            var futures = await db.ClanEvents
                .Where(e => e.SeriesId == sid && e.Status == ClanEventStatus.Scheduled && e.StartUtc > DateTime.UtcNow)
                .ToListAsync();
            foreach (var occ in futures)
                occ.ImageFileName = series.ImageFileName;
            await db.SaveChangesAsync();

            foreach (var occ in futures)
                if (await RerenderImageAsync(db, occ, pending)) rerendered++;

            return ("series", rerendered);
        }

        ev.ImageBytes    = pending.Clear ? null : pending.Bytes;
        ev.ImageFileName = pending.Clear ? null : pending.FileName;
        await db.SaveChangesAsync();

        if (await RerenderImageAsync(db, ev, pending)) rerendered++;
        return ("event", rerendered);
    }

    /// <summary>
    /// Replaces (or removes) the banner on an existing event post in place via a
    /// message edit — swapping the attachment and the embed's image reference,
    /// without deleting/re-posting the message (so RSVPs and position survive).
    /// </summary>
    private async Task<bool> RerenderImageAsync(BotDbContext db, ClanEvent ev, PendingImage pending)
    {
        try
        {
            if (_client.GetChannel(ev.ChannelId) is not IMessageChannel channel) return false;
            if (await channel.GetMessageAsync(ev.MessageId) is not IUserMessage msg) return false;

            var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
            var embed = EventEmbedBuilder.BuildEmbed(ev, rsvps, ev.ImageFileName,
                EventEmbedBuilder.GuildNameResolver(_client, ev.GuildId));
            var comps = ev.Status == ClanEventStatus.Cancelled
                ? Empty()
                : EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked: DateTime.UtcNow >= ev.StartUtc);

            if (pending.Clear || pending.Bytes is not { Length: > 0 } || string.IsNullOrWhiteSpace(ev.ImageFileName))
            {
                await msg.ModifyAsync(m =>
                {
                    m.Embed      = embed;
                    m.Components  = comps;
                    m.Attachments = new List<FileAttachment>(); // drop any existing attachment
                });
            }
            else
            {
                using var fa = new FileAttachment(new MemoryStream(pending.Bytes), ev.ImageFileName);
                await msg.ModifyAsync(m =>
                {
                    m.Embed      = embed;
                    m.Components  = comps;
                    m.Attachments = new[] { fa };
                });
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-render image for event {Id} (msg {MsgId})", ev.Id, ev.MessageId);
            return false;
        }
    }

    private async Task FinalizeImageAsync(SocketMessageComponent c, string content)
    {
        try { await c.ModifyOriginalResponseAsync(m => { m.Content = content; m.Components = Empty(); m.Embed = null; }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to finalize image message"); }
    }

    // ─── Buttons (series branch + abort) ───────────────────────────────────

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        var cid = component.Data.CustomId;
        if (!cid.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (cid == $"{Prefix}abort")
        {
            await component.UpdateAsync(m => { m.Content = "Okay — nothing changed."; m.Components = Empty(); });
            return;
        }

        // On-post Edit/Cancel buttons (evtmgmt:pedit:<id> / evtmgmt:pcancel:<id>).
        // These fire on the public event post, so every reply must be ephemeral.
        if (cid.StartsWith($"{Prefix}pedit:", StringComparison.Ordinal)
         || cid.StartsWith($"{Prefix}pcancel:", StringComparison.Ordinal))
        {
            await OnPostButtonAsync(component, cid);
            return;
        }

        // On-post "Set Host" button → opens an ephemeral guild user-select.
        if (cid.StartsWith($"{Prefix}sethost:", StringComparison.Ordinal))
        {
            await OnSetHostAsync(component, cid);
            return;
        }

        // On-post "Add to Calendar" button → ephemeral Google link + .ics file.
        // Open to everyone; no manage permission required.
        if (cid.StartsWith($"{Prefix}cal:", StringComparison.Ordinal))
        {
            await OnAddToCalendarAsync(component, cid);
            return;
        }

        var parts = cid.Split(':'); // evtmgmt:<cxl|editpick>:<occ|series>:<clanEventId>
        if (parts.Length != 4) return;
        var kind  = parts[1];
        var scope = parts[2];
        if (!int.TryParse(parts[3], out var clanEventId)) return;

        ClanEvent? ev;
        using (var s = _services.CreateScope())
        {
            var db = s.ServiceProvider.GetRequiredService<BotDbContext>();
            ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        }
        if (ev is null) { await component.UpdateAsync(m => { m.Content = "That event is no longer available."; m.Components = Empty(); }); return; }
        if (!await CallerMayManageAsync(component.User, ev.OrganizerId))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to manage that event."; m.Components = Empty(); });
            return;
        }

        if (kind == "cxl")
        {
            // Ack first — a whole-series cancel re-renders every future
            // occurrence's post (two REST calls each) and can exceed Discord's
            // 3-second window. Update to a holding state, then finalize. The ack
            // is best-effort: the cancel (and its GCal Delete enqueue) must run
            // regardless of whether the ack render succeeds.
            try { await component.UpdateAsync(m => { m.Content = "⏳ Cancelling…"; m.Components = Empty(); m.Embed = null; }); }
            catch (Exception ex) { _logger.LogDebug(ex, "Cancel ack (UpdateAsync) failed for event {Id}", clanEventId); }

            var whole = scope == "series";

            // Run the cancel inside a try/catch and report what ACTUALLY happened.
            // Previously a thrown cancel left the "⏳ Cancelling…" holder on screen
            // (looks done) with nothing saved, and a whole-series cancel that
            // matched zero occurrences still claimed success. Both made a failed
            // cancel indistinguishable from a real one. Now we tell the truth, and
            // log it server-side so there's a durable audit trail.
            string msg;
            try
            {
                var count = await DoCancelAsync(clanEventId, whole);

                if (whole && count == 0)
                {
                    msg = $"⚠️ Nothing was cancelled for **{ev.Title}** — it has no upcoming occurrences " +
                          "(it may have already been cancelled). Run `/event list` to check.";
                    _logger.LogWarning(
                        "Cancel by {User} matched 0 upcoming occurrences for series of ClanEvent {Id} ('{Title}')",
                        component.User.Id, clanEventId, ev.Title);
                }
                else
                {
                    msg = whole
                        ? $"✅ Cancelled the series **{ev.Title}** ({count} upcoming occurrence{(count == 1 ? "" : "s")})."
                        : ev.SeriesId.HasValue
                            ? $"✅ Cancelled this occurrence of **{ev.Title}**."
                            : $"✅ Cancelled **{ev.Title}**.";
                    _logger.LogInformation(
                        "Cancel by {User}: {Scope} for ClanEvent {Id} ('{Title}') — {Count} occurrence(s) cancelled",
                        component.User.Id, whole ? "whole series" : "single occurrence", clanEventId, ev.Title, count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cancel FAILED for ClanEvent {Id} ('{Title}') requested by {User}",
                    clanEventId, ev.Title, component.User.Id);
                msg = $"❌ Couldn't cancel **{ev.Title}** — something went wrong and nothing was changed. " +
                      "Please try again; if it keeps failing, let an admin know.";
            }

            try { await component.ModifyOriginalResponseAsync(m => { m.Content = msg; m.Components = Empty(); m.Embed = null; }); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to finalize cancel message for event {Id}", clanEventId); }
        }
        else if (kind == "editpick")
        {
            if (scope == "series" && ev.SeriesId is null)
            {
                await component.UpdateAsync(m => { m.Content = "That isn't a series."; m.Components = Empty(); });
                return;
            }

            var ok = await BeginEditDmAsync(component.User, ev, scope == "series" ? "series" : "occ");
            await component.UpdateAsync(m =>
            {
                m.Content = ok
                    ? "📬 I've sent you a DM to edit this event."
                    : "I couldn't DM you — check that DMs from server members are enabled.";
                m.Components = Empty();
            });
        }
    }

    // ─── On-post Edit/Cancel buttons ───────────────────────────────────────

    /// <summary>
    /// Handles the Edit/Cancel buttons rendered on the public event post. All
    /// replies are ephemeral (or a modal) so the post itself is never touched
    /// here; the actual edit/cancel work happens in the shared modal/cxl paths.
    /// </summary>
    private async Task OnPostButtonAsync(SocketMessageComponent component, string cid)
    {
        var isCancel = cid.StartsWith($"{Prefix}pcancel:", StringComparison.Ordinal);
        var idStr    = cid[(cid.LastIndexOf(':') + 1)..];
        if (!int.TryParse(idStr, out var clanEventId))
        {
            await component.RespondAsync("Couldn't read that event.", ephemeral: true);
            return;
        }

        ClanEvent? ev;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        }

        if (ev is null || ev.Status != ClanEventStatus.Scheduled)
        {
            await component.RespondAsync("That event is no longer available.", ephemeral: true);
            return;
        }
        if (!await CallerMayManageAsync(component.User, ev.OrganizerId))
        {
            await component.RespondAsync("You don't have permission to manage that event.", ephemeral: true);
            return;
        }

        if (isCancel)
        {
            if (ev.SeriesId.HasValue)
            {
                var buttons = new ComponentBuilder()
                    .WithButton("This occurrence", $"{Prefix}cxl:occ:{ev.Id}",    ButtonStyle.Primary)
                    .WithButton("Whole series",    $"{Prefix}cxl:series:{ev.Id}", ButtonStyle.Danger)
                    .WithButton("Never mind",      $"{Prefix}abort",              ButtonStyle.Secondary);
                await component.RespondAsync(
                    $"**{ev.Title}** is a recurring event. Cancel just this occurrence, or the whole series?",
                    components: buttons.Build(), ephemeral: true);
            }
            else
            {
                var buttons = new ComponentBuilder()
                    .WithButton("Yes, cancel", $"{Prefix}cxl:occ:{ev.Id}", ButtonStyle.Danger)
                    .WithButton("Never mind",  $"{Prefix}abort",           ButtonStyle.Secondary);
                await component.RespondAsync(
                    $"Cancel **{ev.Title}**? This removes the post and the calendar entry.",
                    components: buttons.Build(), ephemeral: true);
            }
            return;
        }

        // Edit
        if (ev.SeriesId.HasValue)
        {
            var buttons = new ComponentBuilder()
                .WithButton("This occurrence", $"{Prefix}editpick:occ:{ev.Id}",    ButtonStyle.Primary)
                .WithButton("Whole series",    $"{Prefix}editpick:series:{ev.Id}", ButtonStyle.Danger)
                .WithButton("Never mind",      $"{Prefix}abort",                   ButtonStyle.Secondary);
            await component.RespondAsync(
                $"**{ev.Title}** is a recurring event. Edit just this occurrence, or the whole series?",
                components: buttons.Build(), ephemeral: true);
        }
        else
        {
            var ok = await BeginEditDmAsync(component.User, ev, "single");
            await component.RespondAsync(
                ok ? "📬 I've sent you a DM to edit this event."
                   : "I couldn't DM you — check that DMs from server members are enabled.",
                ephemeral: true);
        }
    }

    // ─── DM edit wizard (numbered-menu editing; staged, applied on "done") ──

    /// <summary>Builds an edit session from the clicked event and DMs the field
    /// menu. Returns false if the DM couldn't be opened (closed DMs).</summary>
    private async Task<bool> BeginEditDmAsync(SocketUser user, ClanEvent ev, string scope)
    {
        IDMChannel dm;
        try { dm = await user.CreateDMChannelAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Couldn't open DM for edit ({User})", user.Id); return false; }

        var session = new EditSession
        {
            UserId          = user.Id,
            Dm              = dm,
            ClanEventId     = ev.Id,
            Scope           = scope,
            SeriesId        = ev.SeriesId,
            Tz              = await ResolveCallerZoneAsync(user.Id),
            Step            = EditStep.Action,
            LastActivityAt  = DateTime.UtcNow,
            Title           = ev.Title,
            StartUtc        = ev.StartUtc,
            EndUtc          = ev.EndUtc,
            Description     = ev.Description ?? string.Empty,
            MaxParticipants = ev.MaxParticipants,
            HasImage        = !string.IsNullOrWhiteSpace(ev.ImageFileName),
        };
        _editSessions[user.Id] = session;

        if (scope == "series" && ev.SeriesId is int sid0)
            await LoadSeriesScheduleAsync(session, sid0);

        try { await SendActionMenuAsync(session); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't send edit menu ({User})", user.Id);
            _editSessions.TryRemove(user.Id, out _);
            return false;
        }
        return true;
    }

    private async Task LoadSeriesScheduleAsync(EditSession s, int seriesId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var series = await db.ClanEventSeries.FirstOrDefaultAsync(x => x.Id == seriesId);
        if (series is null) return;

        s.CurrentFrequency  = series.Frequency;
        s.NewFrequency      = series.Frequency;
        s.NewUntilUtc       = series.UntilUtc;
        s.NewMaxOccurrences = series.MaxOccurrences;

        if (series.Frequency == ClanEventFrequency.Custom)
            s.NewDatesUtc = await db.ClanEvents
                .Where(e => e.SeriesId == seriesId && e.Status == ClanEventStatus.Scheduled && e.StartUtc > DateTime.UtcNow)
                .OrderBy(e => e.StartUtc)
                .Select(e => e.StartUtc)
                .ToListAsync();

        s.ScheduleNowLabel = DescribeScheduleFrom(
            s.CurrentFrequency, s.NewUntilUtc, s.NewMaxOccurrences, s.NewDatesUtc.Count, s.Tz);
    }

    // Whole-series edits exclude time fields — those re-anchor the recurrence and
    // are handled per-occurrence (or by the repeat-schedule field, coming next).
    // Order mirrors Apollo's modify form (Title → Description → Start → Duration …).
    // Field sets by scope. A single occurrence ("occ") deliberately omits the
    // time fields: moving one occurrence's start would orphan its (SeriesId,
    // StartUtc) cadence slot, and the scheduler would then regenerate a duplicate
    // at the original time. To shift a single instance, cancel it and create a
    // one-off (Duplicate). Whole-series time/rhythm changes go through the
    // Repeat-schedule field; one-offs ("single") keep full time control.
    private static EditStep[] MenuFields(EditSession s) =>
        s.Scope == "series"
            ? new[] { EditStep.Title, EditStep.Description, EditStep.Image, EditStep.MaxParticipants, EditStep.Recurrence }
        : s.Scope == "occ"
            ? new[] { EditStep.Title, EditStep.Description, EditStep.Image, EditStep.MaxParticipants }
            : new[] { EditStep.Title, EditStep.Description, EditStep.When, EditStep.Duration, EditStep.Image, EditStep.MaxParticipants, EditStep.Recurrence };

    private static string FieldLabel(EditStep step) => step switch
    {
        EditStep.Title           => "Title",
        EditStep.When            => "Start Time",
        EditStep.Duration        => "Duration",
        EditStep.Description     => "Description",
        EditStep.Image           => "Image",
        EditStep.MaxParticipants => "Attendee limit",
        EditStep.Recurrence      => "Repeat schedule",
        _                        => "",
    };

    // Plain text (no Discord <t:> stamps): these render inside a code-block box,
    // where timestamp markdown would show literally. The DM is the editor's own,
    // so showing their local zone is correct.
    private static string FieldValue(EditSession s, EditStep step) => step switch
    {
        EditStep.Title           => string.IsNullOrWhiteSpace(s.Title) ? "—" : s.Title,
        EditStep.When            => FormatRange(s),
        EditStep.Duration        => $"{Math.Max(1, (int)Math.Round((s.EndUtc - s.StartUtc).TotalMinutes))} min",
        EditStep.Description     => string.IsNullOrWhiteSpace(s.Description) ? "—" : s.Description,
        EditStep.Image           => s.ImageChanged ? (s.ImageCleared ? "Will be removed" : "New image staged") : (s.HasImage ? "Set" : "None"),
        EditStep.MaxParticipants => s.MaxParticipants?.ToString(CultureInfo.InvariantCulture) ?? "No limit",
        EditStep.Recurrence      => DescribeSchedule(s),
        _                        => "—",
    };

    private static bool FieldInline(EditStep step) =>
        step is EditStep.When or EditStep.Duration or EditStep.MaxParticipants;

    private static string FormatRange(EditSession s)
    {
        var startLocal = TimeZoneInfo.ConvertTimeFromUtc(s.StartUtc, s.Tz);
        var endLocal   = TimeZoneInfo.ConvertTimeFromUtc(s.EndUtc, s.Tz);
        return $"{startLocal.ToString("ddd MMM d, yyyy  h:mm tt", CultureInfo.InvariantCulture)} – " +
               $"{endLocal.ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    }

    private static string DescribeSchedule(EditSession s) =>
        DescribeScheduleFrom(s.NewFrequency, s.NewUntilUtc, s.NewMaxOccurrences, s.NewDatesUtc.Count, s.Tz);

    private static string DescribeScheduleFrom(
        ClanEventFrequency? freq, DateTime? untilUtc, int? maxOcc, int dateCount, TimeZoneInfo tz)
    {
        if (freq is null) return "Does not repeat";
        if (freq == ClanEventFrequency.Custom) return $"Specific dates ({dateCount})";

        var word = freq switch
        {
            ClanEventFrequency.Daily    => "Daily",
            ClanEventFrequency.Weekly   => "Weekly",
            ClanEventFrequency.Biweekly => "Biweekly",
            ClanEventFrequency.Monthly  => "Monthly",
            _                           => freq.ToString(),
        };
        if (maxOcc is int m) return $"{word} × {m}";
        if (untilUtc is DateTime u)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(u, tz);
            return $"{word} until {local.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
        }
        return word;
    }

    // Apollo-style grey value box: a code fence renders monospace and boxed.
    private static string FormBox(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "—" : value;
        if (v.Length > 1000) v = v[..1000] + "…";
        return $"```\n{v}\n```";
    }

    private async Task SendEditMenuAsync(EditSession s)
    {
        s.Step = EditStep.Menu;
        var fields = MenuFields(s);

        var eb = new EmbedBuilder()
            .WithColor(new Color(0x5865F2))
            .WithTitle("What would you like to modify?")
            .WithFooter("Type a number to edit • \"done\" to save • \"cancel\" to discard • times out in 15 min");

        var scopeNote = s.Scope == "series"
            ? "Editing the **whole series** — applies to every upcoming occurrence."
            : s.Scope == "occ" ? "Editing **this occurrence** only." : null;
        if (scopeNote is not null) eb.WithDescription(scopeNote);

        for (var i = 0; i < fields.Length; i++)
            eb.AddField($"{i + 1} · {FieldLabel(fields[i])}", FormBox(FieldValue(s, fields[i])), inline: FieldInline(fields[i]));

        await s.Dm.SendMessageAsync(embed: eb.Build());
    }

    // ─── Action menu (Apollo-style "What would you like to do?") ────────────

    private async Task SendActionMenuAsync(EditSession s)
    {
        s.Step = EditStep.Action;
        var scopeNote = s.Scope == "series"
            ? "Recurring event — changes apply to the **whole series**.\n\n"
            : s.Scope == "occ" ? "Recurring event — changes apply to **this occurrence**.\n\n" : "";
        var body = scopeNote +
            "**1 ·** Modify the event\n" +
            "**2 ·** Add a response\n" +
            "**3 ·** Remove a response\n" +
            "**4 ·** Duplicate the event\n" +
            "**5 ·** Clear all responses\n\n" +
            "Enter a number to choose, or **cancel** to exit.";
        await s.Dm.SendMessageAsync(embed: EditForm("What would you like to do?", body));
    }

    private static bool IsFormStep(EditStep step) =>
        step is EditStep.Menu or EditStep.Title or EditStep.When or EditStep.Duration
             or EditStep.Description or EditStep.Image or EditStep.MaxParticipants;

    private async Task HandleActionMenuAsync(EditSession s, string text)
    {
        switch (text.Trim())
        {
            case "1": await SendEditMenuAsync(s); break;     // → field form (sets Step = Menu)
            case "2":
                s.Step = EditStep.AddStatus;
                await s.Dm.SendMessageAsync(embed: EditForm("➕ Add a response",
                    "Which response?\n**1 ·** Going\n**2 ·** Maybe\n**3 ·** Declined"));
                break;
            case "3": await BeginRemoveAsync(s); break;
            case "4": await DuplicateEventAsync(s); break;
            case "5": await BeginClearAsync(s);  break;
            default:
                await s.Dm.SendMessageAsync(embed: EditForm("Pick an option", "Type a number from **1–5** — or **cancel** to exit."));
                break;
        }
    }

    // ── Add a response ──────────────────────────────────────────────────────

    private async Task HandleAddStatusAsync(EditSession s, string text)
    {
        EventRsvpStatus? status = text.Trim() switch
        {
            "1" => EventRsvpStatus.Going,
            "2" => EventRsvpStatus.Maybe,
            "3" => EventRsvpStatus.Decline,
            _   => null,
        };
        if (status is null)
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Pick a response", "Type **1** (Going), **2** (Maybe), or **3** (Declined)."));
            return;
        }
        s.PendingAddStatus = status.Value;
        s.Step = EditStep.AddWho;
        await s.Dm.SendMessageAsync(embed: EditForm("➕ Who?", "Mention the member (or paste their numeric user ID)."));
    }

    private async Task HandleAddWhoAsync(EditSession s, string text)
    {
        if (!TryParseUserId(text, out var userId))
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Couldn't read that",
                "Mention the member like `@Name`, or paste their numeric user ID."));
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == s.ClanEventId);
        if (ev is null || ev.Status != ClanEventStatus.Scheduled)
        {
            await EndEditSessionAsync(s, EditForm("Gone", "That event is no longer available."));
            return;
        }

        var rsvp = await db.EventRsvps.FirstOrDefaultAsync(r => r.ClanEventId == ev.Id && r.UserId == userId);
        if (rsvp is null)
            db.EventRsvps.Add(new EventRsvp { ClanEventId = ev.Id, UserId = userId, Status = s.PendingAddStatus, UpdatedAt = DateTime.UtcNow });
        else
        {
            rsvp.Status    = s.PendingAddStatus;
            rsvp.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Rebalance enforces the cap: a Going add on a full event lands on the waitlist.
        var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
        var promoted = EventWaitlist.Rebalance(rsvps, ev.MaxParticipants);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

        var landed = rsvps.FirstOrDefault(r => r.UserId == userId)?.Status ?? s.PendingAddStatus;
        await UpdatePostAsync(db, ev);
        foreach (var uid in promoted) await EventWaitlist.NotifyPromotedAsync(_client, ev, uid);

        var note = landed == EventRsvpStatus.Waitlisted
            ? $"Added <@{userId}> — the event is full, so they're on the **waitlist**."
            : $"Added <@{userId}> as **{StatusWord(landed)}**.";
        await EndEditSessionAsync(s, EditForm("✅ Response added", note));
    }

    // ── Remove a response ───────────────────────────────────────────────────

    private async Task BeginRemoveAsync(EditSession s)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var rsvps = await db.EventRsvps
            .Where(r => r.ClanEventId == s.ClanEventId)
            .OrderBy(r => r.Status).ThenBy(r => r.UpdatedAt)
            .ToListAsync();

        if (rsvps.Count == 0)
        {
            s.Step = EditStep.Action;
            await s.Dm.SendMessageAsync(embed: EditForm("No responses yet", "There's nothing to remove. Type a menu number, or **cancel**."));
            return;
        }

        s.RemoveCandidates = rsvps.Select(r => (r.UserId, r.Status)).ToList();
        s.Step = EditStep.RemoveWho;

        var lines = new List<string>();
        for (var i = 0; i < s.RemoveCandidates.Count; i++)
            lines.Add($"**{i + 1} ·** <@{s.RemoveCandidates[i].UserId}> — {StatusWord(s.RemoveCandidates[i].Status)}");
        await s.Dm.SendMessageAsync(embed: EditForm("➖ Remove a response",
            string.Join("\n", lines) + "\n\nType the number to remove, or **cancel**."));
    }

    private async Task HandleRemoveWhoAsync(EditSession s, string text)
    {
        if (!int.TryParse(text.Trim(), out var n) || n < 1 || n > s.RemoveCandidates.Count)
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Pick a number", $"Type a number from **1–{s.RemoveCandidates.Count}**, or **cancel**."));
            return;
        }

        var targetId = s.RemoveCandidates[n - 1].UserId;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == s.ClanEventId);
        if (ev is null || ev.Status != ClanEventStatus.Scheduled)
        {
            await EndEditSessionAsync(s, EditForm("Gone", "That event is no longer available."));
            return;
        }

        var rsvp = await db.EventRsvps.FirstOrDefaultAsync(r => r.ClanEventId == ev.Id && r.UserId == targetId);
        if (rsvp is not null) db.EventRsvps.Remove(rsvp);
        await db.SaveChangesAsync();

        var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
        var promoted = EventWaitlist.Rebalance(rsvps, ev.MaxParticipants);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

        await UpdatePostAsync(db, ev);
        foreach (var uid in promoted) await EventWaitlist.NotifyPromotedAsync(_client, ev, uid);

        await EndEditSessionAsync(s, EditForm("✅ Response removed", $"Removed <@{targetId}>'s response."));
    }

    // ── Clear all responses ─────────────────────────────────────────────────

    private async Task BeginClearAsync(EditSession s)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var count = await db.EventRsvps.CountAsync(r => r.ClanEventId == s.ClanEventId);
        if (count == 0)
        {
            s.Step = EditStep.Action;
            await s.Dm.SendMessageAsync(embed: EditForm("Nothing to clear", "There are no responses yet. Type a menu number, or **cancel**."));
            return;
        }
        s.Step = EditStep.ClearConfirm;
        await s.Dm.SendMessageAsync(embed: EditForm("⚠️ Clear all responses",
            $"This removes **all {count}** response{(count == 1 ? "" : "s")} (including the waitlist). Type **yes** to confirm, or **no** to go back."));
    }

    private async Task HandleClearConfirmAsync(EditSession s, string text)
    {
        var t = text.Trim();
        if (t.Equals("no", StringComparison.OrdinalIgnoreCase) || t.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            await SendActionMenuAsync(s);
            return;
        }
        if (!t.Equals("yes", StringComparison.OrdinalIgnoreCase) && !t.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Confirm?", "Type **yes** to clear all responses, or **no** to go back."));
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == s.ClanEventId);
        if (ev is null || ev.Status != ClanEventStatus.Scheduled)
        {
            await EndEditSessionAsync(s, EditForm("Gone", "That event is no longer available."));
            return;
        }

        var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
        if (rsvps.Count > 0) db.EventRsvps.RemoveRange(rsvps);
        await db.SaveChangesAsync();

        await UpdatePostAsync(db, ev);
        await EndEditSessionAsync(s, EditForm("✅ Cleared", "Removed all responses."));
    }

    // ── Duplicate the event ─────────────────────────────────────────────────

    private async Task DuplicateEventAsync(EditSession s)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == s.ClanEventId);
        if (ev is null)
        {
            await EndEditSessionAsync(s, EditForm("Gone", "That event is no longer available."));
            return;
        }

        // Carry the banner over. Series occurrences keep their bytes on the
        // series row, so pull from there when the occurrence has none of its own.
        var imageBytes = ev.ImageBytes;
        if (imageBytes is null && ev.SeriesId is int sid)
            imageBytes = (await db.ClanEventSeries.FirstOrDefaultAsync(x => x.Id == sid))?.ImageBytes;

        // The copy is always a fresh one-off (no recurrence), credited to whoever
        // duplicated it, at the same time — they can shift it via Modify after.
        var organizerName = _client.GetGuild(ev.GuildId)?.GetUser(s.UserId)?.DisplayName ?? ev.OrganizerName;
        var draft = new EventDraft
        {
            GuildId         = ev.GuildId,
            OrganizerId     = s.UserId,
            OrganizerName   = organizerName,
            TimeZoneId      = s.Tz.Id,
            Title           = ev.Title,
            StartUtc        = ev.StartUtc,
            EndUtc          = ev.EndUtc,
            Description     = ev.Description,
            ImageBytes      = imageBytes,
            ImageFileName   = ev.ImageFileName,
            MaxParticipants = ev.MaxParticipants,
            Frequency       = null,
        };

        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        await publisher.PublishOneOffAsync(draft);

        await EndEditSessionAsync(s, EditForm("✅ Duplicated",
            $"Posted a fresh copy of **{ev.Title}** in the events channel — open it to tweak the time or details."));
    }

    // ── shared helpers ──────────────────────────────────────────────────────

    private static string StatusWord(EventRsvpStatus s) => s switch
    {
        EventRsvpStatus.Going      => "Going",
        EventRsvpStatus.Maybe      => "Maybe",
        EventRsvpStatus.Decline    => "Declined",
        EventRsvpStatus.Waitlisted => "Waitlist",
        _                          => s.ToString(),
    };

    private static bool TryParseUserId(string text, out ulong id)
    {
        id = 0;
        text = text.Trim();
        if (text.StartsWith("<@", StringComparison.Ordinal) && text.EndsWith(">", StringComparison.Ordinal))
        {
            var inner = text[2..^1];
            if (inner.StartsWith("&", StringComparison.Ordinal)) return false; // role mention
            if (inner.StartsWith("!", StringComparison.Ordinal)) inner = inner[1..];
            return ulong.TryParse(inner, out id);
        }
        return ulong.TryParse(text, out id);
    }

    private async Task EndEditSessionAsync(EditSession s, Embed msg)
    {
        _editSessions.TryRemove(s.UserId, out _);
        try { await s.Dm.SendMessageAsync(embed: msg); } catch { }
    }

    private async Task OnEditDmAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_editSessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsEditExpired(s))
        {
            _editSessions.TryRemove(message.Author.Id, out _);
            try { await s.Dm.SendMessageAsync(embed: EditForm("⌛ Edit timed out", "That edit expired from inactivity — **nothing was changed**. Hit Edit again to retry.")); } catch { }
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _editSessions.TryRemove(message.Author.Id, out _);
            try { await s.Dm.SendMessageAsync(embed: EditForm("Discarded", "No changes were made.")); } catch { }
            return;
        }

        if (text.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            if (s.Step == EditStep.RecurrenceDates) { await HandleScheduleDatesAsync(s, "done"); return; }
            if (s.Step is EditStep.Recurrence or EditStep.RecurrenceUntil)
            {
                await s.Dm.SendMessageAsync(embed: EditForm("Not yet", "Finish choosing a schedule first — or type **cancel** to discard."));
                return;
            }
            if (IsFormStep(s.Step))
            {
                _editSessions.TryRemove(message.Author.Id, out _);
                try { await ApplyEditsAsync(s); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Applying DM edits failed for event {Id}", s.ClanEventId);
                    try { await s.Dm.SendMessageAsync(embed: EditForm("Something went wrong", "I couldn't apply those edits — check the post to see what stuck.")); } catch { }
                }
            }
            else
            {
                await EndEditSessionAsync(s, EditForm("✅ Done", "Closed the editor — no further changes."));
            }
            return;
        }

        try
        {
            switch (s.Step)
            {
                case EditStep.Action:          await HandleActionMenuAsync(s, text);        break;
                case EditStep.Menu:            await HandleEditMenuAsync(s, text);           break;
                case EditStep.Title:           await HandleEditTitleAsync(s, text);          break;
                case EditStep.When:            await HandleEditWhenAsync(s, text);           break;
                case EditStep.Duration:        await HandleEditDurationAsync(s, text);       break;
                case EditStep.Description:     await HandleEditDescriptionAsync(s, text);    break;
                case EditStep.Image:           await HandleEditImageAsync(s, message, text); break;
                case EditStep.MaxParticipants: await HandleEditMaxAsync(s, text);            break;
                case EditStep.Recurrence:      await HandleScheduleChoiceAsync(s, text);     break;
                case EditStep.RecurrenceUntil: await HandleScheduleUntilAsync(s, text);      break;
                case EditStep.RecurrenceDates: await HandleScheduleDatesAsync(s, text);      break;
                case EditStep.AddStatus:       await HandleAddStatusAsync(s, text);          break;
                case EditStep.AddWho:          await HandleAddWhoAsync(s, text);             break;
                case EditStep.RemoveWho:       await HandleRemoveWhoAsync(s, text);          break;
                case EditStep.ClearConfirm:    await HandleClearConfirmAsync(s, text);       break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Edit DM step {Step} failed for {User}", s.Step, message.Author.Id);
            try { await s.Dm.SendMessageAsync(embed: EditForm("Something went wrong", "Let's go back to the menu.")); } catch { }
            if (IsFormStep(s.Step)) await SendEditMenuAsync(s);
            else                    await SendActionMenuAsync(s);
        }
    }

    private async Task HandleEditMenuAsync(EditSession s, string text)
    {
        var fields = MenuFields(s);
        if (!int.TryParse(text, out var n) || n < 1 || n > fields.Length)
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Pick a field", $"Type a number from **1–{fields.Length}**, or **done** / **cancel**."));
            return;
        }

        s.Step = fields[n - 1];

        if (s.Step == EditStep.Recurrence) { await SendScheduleMenuAsync(s); return; }

        var prompt = s.Step switch
        {
            EditStep.Title           => ("📝 New title", "Send the new title."),
            EditStep.When            => ("🕒 New start time", "Send the new start, e.g. `7pm tomorrow` or `June 14 at 8pm`. The duration stays the same."),
            EditStep.Duration        => ("⏱️ New duration", "Send a duration or end time, e.g. `90 minutes` or `until 9pm`."),
            EditStep.Description     => ("📄 New description", "Send the new description, or `none` to clear it."),
            EditStep.Image           => ("🖼️ New image", "Drag in an image/GIF, paste a link, or type `clear` to remove the banner."),
            EditStep.MaxParticipants => ("👥 New attendee limit", "Send a whole number, or `none` for no limit."),
            _                        => ("Edit", "Send the new value."),
        };
        await s.Dm.SendMessageAsync(embed: EditForm(prompt.Item1, prompt.Item2));
    }

    private async Task HandleEditTitleAsync(EditSession s, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { await s.Dm.SendMessageAsync(embed: EditForm("📝 Title", "Title can't be empty — send some text.")); return; }
        if (text.Length > 100)               { await s.Dm.SendMessageAsync(embed: EditForm("📝 Too long", "Keep it under 100 characters.")); return; }
        s.Title = text.Trim();
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditWhenAsync(EditSession s, string text)
    {
        var r = _time.ParseStart(text, s.Tz);
        if (!r.Success)      { await s.Dm.SendMessageAsync(embed: EditForm("Let's try that again", r.Error)); return; }
        if (!r.HasTimeOfDay) { await s.Dm.SendMessageAsync(embed: EditForm("🕒 Need a time of day", "Include a time too, like `June 14 at 7pm`.")); return; }
        var duration = s.EndUtc - s.StartUtc;
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromHours(1);
        s.StartUtc = r.StartUtc;
        s.EndUtc   = r.StartUtc + duration; // preserve the event's length
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditDurationAsync(EditSession s, string text)
    {
        var d = _time.ParseEnd(text, s.StartUtc, s.Tz);
        if (!d.Success) { await s.Dm.SendMessageAsync(embed: EditForm("Let's try that again", d.Error)); return; }
        s.EndUtc = d.EndUtc;
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditDescriptionAsync(EditSession s, string text)
    {
        s.Description = text.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : text.Trim();
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditMaxAsync(EditSession s, string text)
    {
        text = text.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("unlimited", StringComparison.OrdinalIgnoreCase) || text == "0")
            s.MaxParticipants = null;
        else if (int.TryParse(text, out var n) && n > 0)
            s.MaxParticipants = n;
        else { await s.Dm.SendMessageAsync(embed: EditForm("👥 Need a number", "Give me a whole number greater than 0, or `none`.")); return; }
        await SendEditMenuAsync(s);
    }

    // ── Repeat schedule sub-flow ────────────────────────────────────────────

    private async Task SendScheduleMenuAsync(EditSession s)
    {
        s.Step = EditStep.Recurrence;
        var ctx = s.Scope == "series"
            ? $"Currently: **{s.ScheduleNowLabel}**.\n" +
              "Changing this updates upcoming occurrences — ones that no longer fit the new rhythm are removed (their RSVPs too); ones that still line up keep theirs.\n\n"
            : "Turn this into a repeating series, anchored to the event's current date & time.\n\n";
        var body = ctx +
            "**1 ·** Does not repeat\n" +
            "**2 ·** Daily\n" +
            "**3 ·** Weekly\n" +
            "**4 ·** Biweekly\n" +
            "**5 ·** Monthly\n" +
            "**6 ·** Specific dates\n\n" +
            "Pick a number, or **cancel** to discard everything.";
        await s.Dm.SendMessageAsync(embed: EditForm("🔁 Repeat schedule", body));
    }

    private async Task HandleScheduleChoiceAsync(EditSession s, string text)
    {
        switch (text.Trim())
        {
            case "1": // does not repeat
                s.NewFrequency = null;
                s.NewUntilUtc = null;
                s.NewMaxOccurrences = null;
                s.NewDatesUtc.Clear();
                s.ScheduleChanged = true;
                await SendEditMenuAsync(s);
                break;
            case "2": case "3": case "4": case "5":
                s.NewFrequency = text.Trim() switch
                {
                    "2" => ClanEventFrequency.Daily,
                    "3" => ClanEventFrequency.Weekly,
                    "4" => ClanEventFrequency.Biweekly,
                    "5" => ClanEventFrequency.Monthly,
                    _   => ClanEventFrequency.Weekly,
                };
                s.Step = EditStep.RecurrenceUntil;
                await s.Dm.SendMessageAsync(embed: EditForm("🔁 Until when?",
                    "Give an **end date** (`August 1`), a **number of times** (`8`), or `none` for open-ended."));
                break;
            case "6":
                s.NewFrequency = ClanEventFrequency.Custom;
                await BeginScheduleDatesAsync(s);
                break;
            default:
                await s.Dm.SendMessageAsync(embed: EditForm("Pick an option", "Type a number from **1–6**, or **cancel**."));
                break;
        }
    }

    private async Task HandleScheduleUntilAsync(EditSession s, string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        if (lower is "none" or "forever" or "open" or "ongoing")
        {
            s.NewUntilUtc = null;
            s.NewMaxOccurrences = null;
        }
        else if (int.TryParse(lower, out var count) && count > 0)
        {
            s.NewMaxOccurrences = count;
            s.NewUntilUtc = null;
        }
        else
        {
            var r = _time.ParseStart(text, s.Tz);
            if (!r.Success)
            {
                await s.Dm.SendMessageAsync(embed: EditForm("🔁 Until when?",
                    "Tell me an **end date** (`August 1`), a **number of times** (`8`), or `none`."));
                return;
            }
            s.NewUntilUtc = r.StartUtc;
            s.NewMaxOccurrences = null;
        }
        s.NewDatesUtc.Clear();          // leaving any prior Custom date list behind
        s.ScheduleChanged = true;
        await SendEditMenuAsync(s);
    }

    private async Task BeginScheduleDatesAsync(EditSession s)
    {
        // Seed the working list: an existing Custom series already has it; a rule
        // series seeds from its upcoming occurrences (so you prune what's there); a
        // one-off seeds from its own start.
        if (s.NewDatesUtc.Count == 0)
        {
            if (s.Scope == "series" && s.SeriesId is int sid)
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                s.NewDatesUtc = await db.ClanEvents
                    .Where(e => e.SeriesId == sid && e.Status == ClanEventStatus.Scheduled && e.StartUtc > DateTime.UtcNow)
                    .OrderBy(e => e.StartUtc)
                    .Select(e => e.StartUtc)
                    .ToListAsync();
            }
            if (s.NewDatesUtc.Count == 0)
                s.NewDatesUtc = new List<DateTime> { s.StartUtc };
        }
        await SendScheduleDatesAsync(s);
    }

    private async Task SendScheduleDatesAsync(EditSession s)
    {
        s.Step = EditStep.RecurrenceDates;
        s.NewDatesUtc = s.NewDatesUtc.OrderBy(x => x).ToList();

        var lines = s.NewDatesUtc
            .Select((d, i) =>
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(d, s.Tz);
                return $"**{i + 1} ·** {local.ToString("ddd MMM d, yyyy  h:mm tt", CultureInfo.InvariantCulture)}";
            })
            .ToList();
        var list = lines.Count > 0 ? string.Join("\n", lines) : "_(no dates yet)_";

        var body = list + "\n\n" +
            "Send another **date & time** to add (e.g. `Thursday 8pm`), `remove 2` to drop one, or **done** when finished.";
        await s.Dm.SendMessageAsync(embed: EditForm("🗓️ Specific dates", body));
    }

    private async Task HandleScheduleDatesAsync(EditSession s, string text)
    {
        var t = text.Trim();

        if (t.Equals("done", StringComparison.OrdinalIgnoreCase) || t.Equals("finish", StringComparison.OrdinalIgnoreCase))
        {
            s.ScheduleChanged = true;
            if (s.NewDatesUtc.Count <= 1)
            {
                // Collapsed to one (or zero) date → no longer a recurrence.
                s.NewFrequency = null;
                s.NewUntilUtc = null;
                s.NewMaxOccurrences = null;
                if (s.NewDatesUtc.Count == 1) s.StartUtc = s.NewDatesUtc[0];
            }
            else
            {
                s.NewFrequency = ClanEventFrequency.Custom;
            }
            await SendEditMenuAsync(s);
            return;
        }

        if (t.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
        {
            var rest = t.Length > 6 ? t[6..].Trim() : string.Empty;
            if (int.TryParse(rest, out var idx) && idx >= 1 && idx <= s.NewDatesUtc.Count)
            {
                var ordered = s.NewDatesUtc.OrderBy(x => x).ToList();
                ordered.RemoveAt(idx - 1);
                s.NewDatesUtc = ordered;
                await SendScheduleDatesAsync(s);
            }
            else
            {
                await s.Dm.SendMessageAsync(embed: EditForm("Which one?",
                    $"Type `remove N` where N is between **1** and **{s.NewDatesUtc.Count}**."));
            }
            return;
        }

        var r = _time.ParseStart(t, s.Tz);
        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Let's try that again",
                string.IsNullOrWhiteSpace(r.Error) ? "Try `Thursday 8pm` or `June 20 at 7pm`." : r.Error));
            return;
        }
        if (!r.HasTimeOfDay)
        {
            await s.Dm.SendMessageAsync(embed: EditForm("🕒 Need a time of day", "Include a time too, like `June 20 at 7pm`."));
            return;
        }
        if (r.StartUtc <= DateTime.UtcNow)
        {
            await s.Dm.SendMessageAsync(embed: EditForm("That's in the past", "Pick a future date & time."));
            return;
        }
        if (s.NewDatesUtc.Any(d => d == r.StartUtc))
        {
            await s.Dm.SendMessageAsync(embed: EditForm("Already added", "That date's already in the list."));
            return;
        }
        s.NewDatesUtc.Add(r.StartUtc);
        await SendScheduleDatesAsync(s);
    }

    private async Task HandleEditImageAsync(EditSession s, SocketMessage message, string text)
    {
        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase) || text.Equals("none", StringComparison.OrdinalIgnoreCase) || text.Equals("remove", StringComparison.OrdinalIgnoreCase))
        {
            s.ImageChanged = true; s.ImageCleared = true; s.ImageBytes = null; s.ImageFileName = null;
            await SendEditMenuAsync(s);
            return;
        }

        var att = message.Attachments.FirstOrDefault();
        if (att is null)
        {
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await s.Dm.SendMessageAsync("⏳ Fetching that image…");
                var res = await EventImageFetcher.FromUrlAsync(text);
                if (!res.Ok) { await s.Dm.SendMessageAsync(embed: EditForm("🖼️ Couldn't use that link", $"{res.Error} Try another, drag a file in, or type `clear`.")); return; }
                s.ImageChanged = true; s.ImageCleared = false;
                s.ImageFileName = res.FileName;
                s.ImageBytes    = EventImage.Downscale(res.Bytes!, res.FileName);
                await SendEditMenuAsync(s);
                return;
            }
            await s.Dm.SendMessageAsync(embed: EditForm("🖼️ New image", "Drag an image into this DM, paste a link, or type `clear`."));
            return;
        }

        var looksImage = (att.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false) || EventImage.IsAllowedExtension(att.Filename);
        if (!looksImage)               { await s.Dm.SendMessageAsync(embed: EditForm("🖼️ Not an image", "That doesn't look like a PNG/JPG/GIF/WebP. Try another, or type `clear`.")); return; }
        if (att.Size > EventImage.MaxBytes) { await s.Dm.SendMessageAsync(embed: EditForm("🖼️ Too large", $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB.")); return; }

        byte[] bytes;
        try { bytes = await Http.GetByteArrayAsync(att.Url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Edit image download failed for {User}", s.UserId);
            await s.Dm.SendMessageAsync(embed: EditForm("🖼️ Download failed", "Couldn't download that. Try again, or type `clear`."));
            return;
        }
        if (bytes.Length > EventImage.MaxBytes) { await s.Dm.SendMessageAsync(embed: EditForm("🖼️ Too large", $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB.")); return; }

        var name = EventImage.Sanitize(att.Filename);
        s.ImageChanged = true; s.ImageCleared = false;
        s.ImageFileName = name;
        s.ImageBytes    = EventImage.Downscale(bytes, name);
        await SendEditMenuAsync(s);
    }

    /// <summary>Applies the staged edits to the live event (or series + future
    /// occurrences) and re-renders the post(s). Called when the user types "done".</summary>
    private async Task ApplyEditsAsync(EditSession s)
    {
        var pending = s.ImageChanged
            ? new PendingImage(s.ImageCleared ? null : s.ImageBytes, s.ImageCleared ? null : s.ImageFileName, s.ImageCleared, DateTime.UtcNow)
            : null;

        var promoted = new List<(ClanEvent Ev, ulong UserId)>();
        string summary;

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            if (s.Scope == "series")
            {
                if (s.SeriesId is not int sid) { await s.Dm.SendMessageAsync(embed: EditForm("Gone", "That series no longer exists.")); return; }
                var series = await db.ClanEventSeries.FirstOrDefaultAsync(x => x.Id == sid);
                if (series is null)            { await s.Dm.SendMessageAsync(embed: EditForm("Gone", "That series no longer exists.")); return; }

                series.Title           = s.Title;
                series.Description     = s.Description;
                series.MaxParticipants = s.MaxParticipants;
                if (pending is not null)
                {
                    series.ImageBytes    = pending.Clear ? null : pending.Bytes;
                    series.ImageFileName = pending.Clear ? null : pending.FileName;
                }

                var futures = await db.ClanEvents
                    .Where(e => e.SeriesId == sid && e.Status == ClanEventStatus.Scheduled && e.StartUtc > DateTime.UtcNow)
                    .ToListAsync();
                foreach (var occ in futures)
                {
                    occ.Title           = s.Title;
                    occ.Description     = s.Description;
                    occ.MaxParticipants = s.MaxParticipants;
                    if (pending is not null) occ.ImageFileName = series.ImageFileName;
                    await ApplyCalendarUpdateAsync(db, occ);
                }
                await db.SaveChangesAsync();

                foreach (var occ in futures)
                {
                    var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == occ.Id).ToListAsync();
                    foreach (var uid in EventWaitlist.Rebalance(rsvps, occ.MaxParticipants)) promoted.Add((occ, uid));
                }
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

                foreach (var occ in futures)
                {
                    if (pending is not null) await RerenderImageAsync(db, occ, pending);
                    else                     await UpdatePostAsync(db, occ);
                }

                summary = $"the series **{s.Title}** and {futures.Count} upcoming occurrence{(futures.Count == 1 ? "" : "s")}";
            }
            else
            {
                var ev = await db.ClanEvents.FirstOrDefaultAsync(x => x.Id == s.ClanEventId);
                if (ev is null || ev.Status != ClanEventStatus.Scheduled) { await s.Dm.SendMessageAsync(embed: EditForm("Gone", "That event is no longer available.")); return; }

                var startMoved = ev.StartUtc != s.StartUtc;
                ev.Title           = s.Title;
                ev.StartUtc        = s.StartUtc;
                ev.EndUtc          = s.EndUtc;
                ev.Description     = s.Description;
                ev.MaxParticipants = s.MaxParticipants;
                if (pending is not null)
                {
                    ev.ImageBytes    = pending.Clear ? null : pending.Bytes;
                    ev.ImageFileName = pending.Clear ? null : pending.FileName;
                }
                if (startMoved) ev.RemindersSentCsv = string.Empty; // re-arm reminders

                await ApplyCalendarUpdateAsync(db, ev);
                await db.SaveChangesAsync();

                var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
                foreach (var uid in EventWaitlist.Rebalance(rsvps, ev.MaxParticipants)) promoted.Add((ev, uid));
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

                if (pending is not null) await RerenderImageAsync(db, ev, pending);
                else                     await UpdatePostAsync(db, ev);

                summary = $"**{s.Title}**";
            }
        }

        foreach (var (ev, uid) in promoted)
            await EventWaitlist.NotifyPromotedAsync(_client, ev, uid);

        string? scheduleNote = null;
        if (s.ScheduleChanged)
        {
            try { scheduleNote = await ApplyScheduleChangeAsync(s); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Applying schedule change failed for event {Id}", s.ClanEventId);
                scheduleNote = "…but the repeat-schedule change hit a snag — check the events channel.";
            }
        }

        var note = string.IsNullOrWhiteSpace(scheduleNote) ? "" : "\n\n" + scheduleNote;
        await s.Dm.SendMessageAsync(embed: EditForm("✅ Saved", $"Updated {summary}.{note}"));
    }

    // ─── Repeat-schedule application ────────────────────────────────────────
    // Runs after the field edits commit. Routes to one of four transitions based
    // on what the event is now and what the staged target is. Regeneration is
    // "preserve-matching": occurrences whose start still lands on the new rhythm
    // are kept (RSVPs intact); only the slots that actually changed churn.

    private async Task<string> ApplyScheduleChangeAsync(EditSession s)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var now = DateTime.UtcNow;

        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == s.ClanEventId);
        if (ev is null) return "The event went away before the schedule change could apply.";

        // One-off (no series yet).
        if (ev.SeriesId is not int seriesId)
        {
            if (s.NewFrequency is null) return string.Empty; // stayed a one-off — nothing to do
            return await StartRecurringFromOneOffAsync(db, publisher, ev, s, now);
        }

        var series = await db.ClanEventSeries.FirstOrDefaultAsync(x => x.Id == seriesId);
        if (series is null) return "The series went away before the schedule change could apply.";

        if (s.NewFrequency is null)
            return await StopRepeatingAsync(db, series, ev, now);

        if (s.NewFrequency == ClanEventFrequency.Custom)
            return await ReconcileCustomDatesAsync(db, publisher, series, s, now);

        return await RegenerateRuleAsync(db, publisher, series, s, now);
    }

    /// <summary>One-off → recurring: build a series anchored on this event, link the
    /// event in as its first occurrence, and materialize the rest forward.</summary>
    private async Task<string> StartRecurringFromOneOffAsync(
        BotDbContext db, IEventPublisher publisher, ClanEvent ev, EditSession s, DateTime now)
    {
        var zone       = await ResolveCallerZoneAsync(ev.OrganizerId);
        var firstLocal = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(ev.StartUtc, zone), DateTimeKind.Unspecified);
        var durMinutes = Math.Max(1, (int)Math.Round((ev.EndUtc - ev.StartUtc).TotalMinutes));
        var isCustom   = s.NewFrequency == ClanEventFrequency.Custom;

        var series = new ClanEventSeries
        {
            GuildId         = ev.GuildId,
            Title           = ev.Title,
            Description     = ev.Description,
            OrganizerId     = ev.OrganizerId,
            OrganizerName   = ev.OrganizerName,
            Frequency       = s.NewFrequency!.Value,
            TimeZoneId      = zone.Id,
            FirstStartLocal = firstLocal,
            DurationMinutes = durMinutes,
            ChannelId       = _config.GetEventPostChannelId(),
            UntilUtc        = isCustom ? null : s.NewUntilUtc,
            MaxOccurrences  = isCustom ? null : s.NewMaxOccurrences,
            MaxParticipants = ev.MaxParticipants,
            Active          = true,
            CreatedAt       = now,
            ImageBytes      = ev.ImageBytes,
            ImageFileName   = ev.ImageFileName,
        };
        db.ClanEventSeries.Add(series);
        await db.SaveChangesAsync(); // materialize series.Id

        // Link this event in as the series' first occurrence. The banner bytes now
        // live on the series row; the already-posted message keeps its attachment.
        ev.SeriesId   = series.Id;
        ev.ImageBytes = null;
        await db.SaveChangesAsync();

        if (isCustom)
        {
            var dates = new List<DateTime>(s.NewDatesUtc);
            if (!dates.Contains(ev.StartUtc)) dates.Add(ev.StartUtc);
            await publisher.MaterializeDatesAsync(series, dates); // ev's own start is skipped (idempotent)
            return $"**{ev.Title}** now runs on {dates.Distinct().Count()} specific dates.";
        }

        await publisher.FillHorizonAsync(series); // first occurrence (== ev) skipped (idempotent)
        return $"**{ev.Title}** now repeats — {DescribeScheduleFrom(series.Frequency, series.UntilUtc, series.MaxOccurrences, 0, s.Tz)}.";
    }

    /// <summary>Series → does not repeat: retire the series, keep a single event
    /// (the clicked occurrence if upcoming, else the next one), cancel the rest.</summary>
    private async Task<string> StopRepeatingAsync(BotDbContext db, ClanEventSeries series, ClanEvent ev, DateTime now)
    {
        series.Active = false;

        var futures = await db.ClanEvents
            .Where(e => e.SeriesId == series.Id && e.Status == ClanEventStatus.Scheduled && e.StartUtc > now)
            .OrderBy(e => e.StartUtc)
            .ToListAsync();

        var survivor = (ev.Status == ClanEventStatus.Scheduled && ev.StartUtc > now)
            ? futures.FirstOrDefault(f => f.Id == ev.Id) ?? ev
            : futures.FirstOrDefault();

        var cancelled = new List<ClanEvent>();
        foreach (var occ in futures)
        {
            if (survivor is not null && occ.Id == survivor.Id) continue;
            await CancelOccurrenceAsync(db, occ);
            cancelled.Add(occ);
        }

        if (survivor is not null)
        {
            survivor.SeriesId = null; // becomes a true one-off
            if (survivor.ImageBytes is null && series.ImageBytes is not null)
            {
                survivor.ImageBytes    = series.ImageBytes;
                survivor.ImageFileName = series.ImageFileName;
            }
        }

        await db.SaveChangesAsync();
        foreach (var occ in cancelled) await UpdatePostAsync(db, occ);

        var n = cancelled.Count;
        return survivor is not null
            ? $"Stopped repeating — kept the {EventTimeParser.Stamp(survivor.StartUtc, 'f')} event and removed {n} upcoming occurrence{(n == 1 ? "" : "s")}."
            : $"Stopped repeating — removed {n} upcoming occurrence{(n == 1 ? "" : "s")}.";
    }

    /// <summary>Rule-based frequency/end change with preserve-matching: cancel only
    /// the upcoming occurrences that no longer fall on the new rhythm, then fill in
    /// the new ones (idempotent, so survivors are untouched and keep their RSVPs).</summary>
    private async Task<string> RegenerateRuleAsync(
        BotDbContext db, IEventPublisher publisher, ClanEventSeries series, EditSession s, DateTime now)
    {
        series.Frequency      = s.NewFrequency!.Value;
        series.UntilUtc       = s.NewUntilUtc;
        series.MaxOccurrences = s.NewMaxOccurrences;
        series.Active         = true;
        await db.SaveChangesAsync(); // persist the new rule before computing/filling

        var horizonEnd = now.AddDays(_config.EventRecurrenceHorizonDays);
        var newSet = ClanEventRecurrence
            .Occurrences(series, now.AddMinutes(-1), horizonEnd, _config.EventRecurrenceMaxBackfill)
            .Select(o => o.StartUtc)
            .ToHashSet();

        var futures = await db.ClanEvents
            .Where(e => e.SeriesId == series.Id && e.Status == ClanEventStatus.Scheduled && e.StartUtc > now)
            .ToListAsync();
        var existing = futures.Select(f => f.StartUtc).ToHashSet();

        var dropped = new List<ClanEvent>();
        foreach (var occ in futures)
            if (!newSet.Contains(occ.StartUtc)) { await CancelOccurrenceAsync(db, occ); dropped.Add(occ); }
        await db.SaveChangesAsync();
        foreach (var occ in dropped) await UpdatePostAsync(db, occ);

        await publisher.FillHorizonAsync(series);

        var kept  = futures.Count - dropped.Count;
        var added = newSet.Count(d => !existing.Contains(d));
        return $"Now {DescribeScheduleFrom(series.Frequency, series.UntilUtc, series.MaxOccurrences, 0, s.Tz)} — " +
               $"kept {kept}, removed {dropped.Count}, added {added} upcoming occurrence{(added == 1 ? "" : "s")}.";
    }

    /// <summary>Custom (specific-dates) reconcile with preserve-matching. Collapses
    /// to a single event if the edited list ends up with one date or fewer.</summary>
    private async Task<string> ReconcileCustomDatesAsync(
        BotDbContext db, IEventPublisher publisher, ClanEventSeries series, EditSession s, DateTime now)
    {
        var target = s.NewDatesUtc.Where(d => d > now).Distinct().OrderBy(x => x).ToList();

        if (target.Count <= 1)
        {
            series.Active = false;
            var keep = target.FirstOrDefault();

            var futuresC = await db.ClanEvents
                .Where(e => e.SeriesId == series.Id && e.Status == ClanEventStatus.Scheduled && e.StartUtc > now)
                .OrderBy(e => e.StartUtc)
                .ToListAsync();

            var survivor = keep != default ? futuresC.FirstOrDefault(f => f.StartUtc == keep) : null;
            survivor ??= futuresC.FirstOrDefault(f => f.Id == s.ClanEventId) ?? futuresC.FirstOrDefault();

            var cancelledC = new List<ClanEvent>();
            foreach (var occ in futuresC)
            {
                if (survivor is not null && occ.Id == survivor.Id) continue;
                await CancelOccurrenceAsync(db, occ);
                cancelledC.Add(occ);
            }
            if (survivor is not null)
            {
                survivor.SeriesId = null;
                if (survivor.ImageBytes is null && series.ImageBytes is not null)
                {
                    survivor.ImageBytes    = series.ImageBytes;
                    survivor.ImageFileName = series.ImageFileName;
                }
            }
            await db.SaveChangesAsync();
            foreach (var occ in cancelledC) await UpdatePostAsync(db, occ);

            return survivor is not null
                ? $"Reduced to a single event on {EventTimeParser.Stamp(survivor.StartUtc, 'f')}; removed {cancelledC.Count} other date{(cancelledC.Count == 1 ? "" : "s")}."
                : $"Removed {cancelledC.Count} date{(cancelledC.Count == 1 ? "" : "s")}.";
        }

        series.Frequency      = ClanEventFrequency.Custom;
        series.UntilUtc       = null;
        series.MaxOccurrences = null;
        series.Active         = true;
        await db.SaveChangesAsync();

        var futures = await db.ClanEvents
            .Where(e => e.SeriesId == series.Id && e.Status == ClanEventStatus.Scheduled && e.StartUtc > now)
            .ToListAsync();
        var existing  = futures.Select(f => f.StartUtc).ToHashSet();
        var targetSet = target.ToHashSet();

        var dropped = new List<ClanEvent>();
        foreach (var occ in futures)
            if (!targetSet.Contains(occ.StartUtc)) { await CancelOccurrenceAsync(db, occ); dropped.Add(occ); }
        await db.SaveChangesAsync();
        foreach (var occ in dropped) await UpdatePostAsync(db, occ);

        var toCreate = target.Where(d => !existing.Contains(d)).ToList();
        if (toCreate.Count > 0) await publisher.MaterializeDatesAsync(series, toCreate);

        var kept = futures.Count - dropped.Count;
        return $"Updated specific dates — kept {kept}, removed {dropped.Count}, added {toCreate.Count}.";
    }

    private async Task SweepEditSessionsAsync()
    {
        try
        {
            foreach (var kv in _editSessions)
            {
                if (!IsEditExpired(kv.Value)) continue;
                if (_editSessions.TryRemove(kv.Key, out var s))
                {
                    try { await s.Dm.SendMessageAsync(embed: EditForm("⌛ Edit timed out", "I didn't hear back, so this edit was dropped and **nothing was changed**.")); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Edit timeout DM failed for {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Edit idle sweep failed"); }
    }

    private static bool IsEditExpired(EditSession s) => DateTime.UtcNow - s.LastActivityAt > EditIdleTimeout;

    private static Embed EditForm(string title, string? body = null)
    {
        var eb = new EmbedBuilder()
            .WithColor(new Color(0x5865F2))
            .WithTitle(title)
            .WithFooter("Reply in this DM • \"done\" saves • \"cancel\" discards • times out after 15 min");
        if (!string.IsNullOrWhiteSpace(body)) eb.WithDescription(body);
        return eb.Build();
    }

    // ─── Set Host (on-post button → user-select) ───────────────────────────

    private async Task OnSetHostAsync(SocketMessageComponent component, string cid)
    {
        var idStr = cid[(cid.LastIndexOf(':') + 1)..];
        if (!int.TryParse(idStr, out var clanEventId))
        {
            await component.RespondAsync("Couldn't read that event.", ephemeral: true);
            return;
        }

        ClanEvent? ev;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        }
        if (ev is null || ev.Status != ClanEventStatus.Scheduled)
        {
            await component.RespondAsync("That event is no longer available.", ephemeral: true);
            return;
        }
        if (!await CallerMayManageAsync(component.User, ev.OrganizerId))
        {
            await component.RespondAsync("You don't have permission to manage that event.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}hostpick:{ev.Id}")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder("Choose the host")
            .WithMinValues(1)
            .WithMaxValues(1);

        await component.RespondAsync(
            $"Who's hosting **{ev.Title}**?",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    private async Task OnAddToCalendarAsync(SocketMessageComponent component, string cid)
    {
        var idStr = cid[(cid.LastIndexOf(':') + 1)..];
        if (!int.TryParse(idStr, out var clanEventId))
        {
            await component.RespondAsync("Couldn't read that event.", ephemeral: true);
            return;
        }

        ClanEvent? ev;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        }
        if (ev is null)
        {
            await component.RespondAsync("That event is no longer available.", ephemeral: true);
            return;
        }

        var comps = new ComponentBuilder()
            .WithButton("Google Calendar", style: ButtonStyle.Link,
                url: EventCalendarLinks.GoogleUrl(ev), emote: new Emoji("📅"))
            .Build();

        using var fa = new FileAttachment(
            new MemoryStream(EventCalendarLinks.IcsBytes(ev)), EventCalendarLinks.IcsFileName(ev));

        await component.RespondWithFileAsync(
            fa,
            text: $"📅 Add **{ev.Title}** to your calendar:\n" +
                  "• **Google Calendar** — tap the button below.\n" +
                  "• **Apple Calendar / Outlook** — open the attached `.ics` file.",
            components: comps,
            ephemeral: true);
    }

    private async Task ApplyHostAsync(SocketMessageComponent component, string id)
    {
        var idStr = id[(id.LastIndexOf(':') + 1)..];
        if (!int.TryParse(idStr, out var clanEventId)
            || !ulong.TryParse(component.Data.Values.FirstOrDefault(), out var hostId))
        {
            await component.UpdateAsync(m => { m.Content = "Something went wrong reading that selection."; m.Components = Empty(); });
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        if (ev is null || ev.Status != ClanEventStatus.Scheduled)
        {
            await component.UpdateAsync(m => { m.Content = "That event is no longer available."; m.Components = Empty(); });
            return;
        }
        if (!await CallerMayManageAsync(component.User, ev.OrganizerId))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to manage that event."; m.Components = Empty(); });
            return;
        }

        ev.HostId = hostId;
        // Push the new host to Google Calendar too (rebuilds the "Host:" line in
        // the event body), not just the Discord post.
        await ApplyCalendarUpdateAsync(db, ev);
        await db.SaveChangesAsync();
        await UpdatePostAsync(db, ev);

        // DM the new host so they know they're running it — but not when the
        // setter picked themselves (they just did it) or when host == creator
        // pattern isn't relevant here since this is an explicit change.
        var note = string.Empty;
        if (hostId != component.User.Id)
            note = await NotifyHostAsync(ev, hostId, component.User);

        await component.UpdateAsync(m =>
        {
            m.Content    = $"✅ Host set to <@{hostId}>.{note}";
            m.Components  = Empty();
            m.AllowedMentions = AllowedMentions.None; // confirm shouldn't ping the new host
        });
    }

    /// <summary>
    /// DMs the newly-assigned host (best-effort). Returns a short note for the
    /// confirmation if the DM couldn't be delivered (e.g. DMs disabled).
    /// </summary>
    private async Task<string> NotifyHostAsync(ClanEvent ev, ulong hostId, SocketUser setter)
    {
        try
        {
            var host = (_client.GetGuild(ev.GuildId)?.GetUser(hostId) as IUser) ?? _client.GetUser(hostId);
            if (host is null) return " (couldn't notify them — they weren't found.)";

            var setterName = (setter as SocketGuildUser)?.DisplayName ?? setter.GlobalName ?? setter.Username;
            var jump = $"https://discord.com/channels/{ev.GuildId}/{ev.ChannelId}/{ev.MessageId}";

            var embed = new EmbedBuilder()
                .WithColor(new Color(0x5865F2))
                .WithTitle("📣 You've been set as event host")
                .WithDescription(
                    $"{setterName} set you as the host of **{ev.Title}**.\n\n" +
                    $"🕒 {EventTimeParser.Stamp(ev.StartUtc, 'F')} ({EventTimeParser.Stamp(ev.StartUtc, 'R')})\n\n" +
                    $"[Jump to the event]({jump})")
                .Build();

            var dm = await host.CreateDMChannelAsync();
            await dm.SendMessageAsync(embed: embed);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to DM new host {Host} for event {Id}", hostId, ev.Id);
            return " (couldn't DM them — they may have DMs disabled.)";
        }
    }

    // ─── Cancel core ───────────────────────────────────────────────────────

    /// <summary>Cancels one occurrence or a whole series. Returns the number of occurrences cancelled.</summary>
    private async Task<int> DoCancelAsync(int clanEventId, bool wholeSeries)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        if (ev is null) return 0;

        List<ClanEvent> targets;
        if (wholeSeries && ev.SeriesId is int seriesId)
        {
            var series = await db.ClanEventSeries.FirstOrDefaultAsync(s => s.Id == seriesId);
            if (series is not null) series.Active = false; // stops the scheduler regenerating it

            var now = DateTime.UtcNow;
            targets = await db.ClanEvents
                .Where(e => e.SeriesId == seriesId && e.Status == ClanEventStatus.Scheduled && e.StartUtc > now)
                .ToListAsync();
            if (!targets.Any(t => t.Id == ev.Id)) targets.Add(ev);
        }
        else
        {
            targets = new List<ClanEvent> { ev };
        }

        foreach (var t in targets)
            await CancelOccurrenceAsync(db, t);

        await db.SaveChangesAsync();

        foreach (var t in targets)
            await UpdatePostAsync(db, t);

        return targets.Count;
    }

    /// <summary>
    /// Marks an occurrence cancelled, enqueues a GCal Delete (GoogleEventId in
    /// payload since the CalendarEvent row is removed), and removes the
    /// CalendarEvent so attendance stops and any pending Create no-ops.
    /// </summary>
    private async Task CancelOccurrenceAsync(BotDbContext db, ClanEvent ev)
    {
        if (ev.Status == ClanEventStatus.Cancelled) return;

        var cal = await db.CalendarEvents.FirstOrDefaultAsync(c => c.Id == ev.CalendarEventId);
        if (cal is not null)
        {
            db.CalendarOutbox.Add(new CalendarOutbox
            {
                GuildId         = ev.GuildId,
                Operation       = CalendarOutboxOperation.Delete,
                CalendarEventId = cal.Id,
                PayloadJson     = JsonConvert.SerializeObject(new CalendarOutboxPayload
                {
                    Title         = ev.Title,
                    StartUtc      = ev.StartUtc,
                    EndUtc        = ev.EndUtc,
                    Source        = "Clan",
                    GoogleEventId = cal.CalendarEventId, // may be empty if Create hasn't landed; worker no-ops then
                }),
                NextAttemptAt = DateTime.UtcNow,
                CreatedAt     = DateTime.UtcNow,
            });
            db.CalendarEvents.Remove(cal);
        }

        ev.Status      = ClanEventStatus.Cancelled;
        ev.CancelledAt = DateTime.UtcNow;
    }

    // ─── Shared helpers ────────────────────────────────────────────────────

    /// <summary>Patches the CalendarEvent fields from the ClanEvent and enqueues a GCal Update.</summary>
    private async Task ApplyCalendarUpdateAsync(BotDbContext db, ClanEvent ev)
    {
        var cal = await db.CalendarEvents.FirstOrDefaultAsync(c => c.Id == ev.CalendarEventId);
        if (cal is null) return;

        cal.Title       = ev.Title;
        cal.StartUtc    = ev.StartUtc;
        cal.EndUtc      = ev.EndUtc;
        cal.Description = ev.Description;

        db.CalendarOutbox.Add(new CalendarOutbox
        {
            GuildId         = ev.GuildId,
            Operation       = CalendarOutboxOperation.Update,
            CalendarEventId = cal.Id,
            PayloadJson     = JsonConvert.SerializeObject(new CalendarOutboxPayload
            {
                Title         = ev.Title,
                StartUtc      = ev.StartUtc,
                EndUtc        = ev.EndUtc,
                Description   = ev.Description,
                // Carried so the worker can rebuild the "Created by:" / "Host:"
                // header on update instead of stripping it. HostName resolves the
                // effective host (explicit host, else the creator).
                OrganizerName = ev.OrganizerName,
                OrganizerId   = ev.OrganizerId,
                HostName      = ResolveHostName(ev),
                Source        = "Clan",
            }),
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt     = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Resolves the display name of an event's effective host for the calendar
    /// body — the explicitly-set host, or the creator when none is set (mirrors
    /// the embed's <c>HostId ?? OrganizerId</c> fallback). Prefers the live guild
    /// display name; falls back to the stored organizer name when the host can't
    /// be resolved (e.g. they've left the guild).
    /// </summary>
    private string ResolveHostName(ClanEvent ev)
    {
        var hostId = ev.HostId ?? ev.OrganizerId;

        var guildName = _client.GetGuild(ev.GuildId)?.GetUser(hostId)?.DisplayName;
        if (!string.IsNullOrWhiteSpace(guildName)) return guildName;

        if (hostId == ev.OrganizerId && !string.IsNullOrWhiteSpace(ev.OrganizerName))
            return ev.OrganizerName;

        var user = _client.GetUser(hostId);
        return user?.GlobalName ?? user?.Username ?? ev.OrganizerName;
    }

    /// <summary>
    /// Re-renders an event's #events post from current state — or, when the event
    /// has been cancelled, deletes the post from the channel entirely.
    /// </summary>
    private async Task UpdatePostAsync(BotDbContext db, ClanEvent ev)
    {
        try
        {
            if (_client.GetChannel(ev.ChannelId) is not IMessageChannel channel) return;
            if (await channel.GetMessageAsync(ev.MessageId) is not IUserMessage msg) return;

            // Cancelled → remove the post from the channel (calendar Delete is
            // enqueued separately in CancelOccurrenceAsync).
            if (ev.Status == ClanEventStatus.Cancelled)
            {
                await msg.DeleteAsync();
                return;
            }

            var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
            var embed = EventEmbedBuilder.BuildEmbed(ev, rsvps, ev.ImageFileName,
                EventEmbedBuilder.GuildNameResolver(_client, ev.GuildId));
            var comps = EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked: DateTime.UtcNow >= ev.StartUtc);

            await msg.ModifyAsync(m => { m.Embed = embed; m.Components = comps; });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update post for event {ClanId} (msg {MsgId})", ev.Id, ev.MessageId);
        }
    }

    private async Task<TimeZoneInfo> ResolveCallerZoneAsync(ulong userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var iana = (await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == userId))?.IanaId;
        return _time.ResolveZone(iana);
    }

    private async Task<bool> CallerMayManageAsync(SocketUser user, ulong organizerId)
    {
        if (user.Id == organizerId) return true;
        if (user is SocketGuildUser gu) return HasEventPermission(gu);

        // Fall back: resolve the guild member to check rank.
        await Task.CompletedTask;
        return false;
    }

    private bool HasEventPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(_config.EventCommandMinRank);
        if (minIndex < 0) return false;

        var highest = user.Roles.Select(r => rankRoles.IndexOf(r.Name)).DefaultIfEmpty(-1).Max();
        return highest >= minIndex;
    }

    private static MessageComponent Empty() => new ComponentBuilder().Build();

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private static string FormatRelative(TimeSpan until)
    {
        if (until.TotalDays >= 1) return $"in {(int)until.TotalDays}d {until.Hours}h";
        if (until.TotalHours >= 1) return $"in {(int)until.TotalHours}h {until.Minutes}m";
        return $"in {Math.Max(1, (int)until.TotalMinutes)}m";
    }
}
