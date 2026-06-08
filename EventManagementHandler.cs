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
    }

    public void Register(DiscordSocketClient client)
    {
        client.SelectMenuExecuted += OnSelectAsync;
        client.ButtonExecuted     += OnButtonAsync;
        client.ModalSubmitted     += OnModalAsync;
    }

    // ─── Entry points (called by EventCommandHandler) ──────────────────────

    public Task StartCancelAsync(SocketSlashCommand command) => ShowPickerAsync(command, "cancel");
    public Task StartEditAsync(SocketSlashCommand command)   => ShowPickerAsync(command, "edit");

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
        await command.FollowupAsync(
            count <= 1 ? "Nothing to sort — there's at most one upcoming event." : $"✅ Re-posted {count} events in chronological order.",
            ephemeral: true);
    }

    private async Task ShowPickerAsync(SocketSlashCommand command, string action)
    {
        await command.DeferAsync(ephemeral: true);

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
            await command.FollowupAsync($"You have no upcoming events to {action}.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}{action}:pick")
            .WithPlaceholder($"Choose an event to {action}")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var ev in events)
        {
            var label = Truncate(ev.Title, 100);
            var desc  = Truncate($"{(ev.SeriesId.HasValue ? "🔁 " : "")}starts {FormatRelative(ev.StartUtc - now)}", 100);
            menu.AddOption(label, ev.Id.ToString(), desc);
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await command.FollowupAsync($"Which event would you like to {action}?", components: components, ephemeral: true);
    }

    // ─── Select menu ───────────────────────────────────────────────────────

    private async Task OnSelectAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        if (id is not ($"{Prefix}cancel:pick" or $"{Prefix}edit:pick"))
            return;

        if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var clanEventId))
        {
            await component.UpdateAsync(m => { m.Content = "Something went wrong reading that selection."; m.Components = Empty(); });
            return;
        }

        var isCancel = id == $"{Prefix}cancel:pick";

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
            await component.RespondWithModalAsync(BuildEditModal("single", ev.Id, ev, withWhen: true, withDuration: true, await ResolveCallerZoneAsync(component.User.Id)));
        }
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
            var whole = scope == "series";
            var count = await DoCancelAsync(clanEventId, whole);
            var msg = whole
                ? $"✅ Cancelled the series **{ev.Title}** ({count} upcoming occurrence{(count == 1 ? "" : "s")})."
                : $"✅ Cancelled this occurrence of **{ev.Title}**.";
            await component.UpdateAsync(m => { m.Content = msg; m.Components = Empty(); });
        }
        else if (kind == "editpick")
        {
            var tz = await ResolveCallerZoneAsync(component.User.Id);
            if (scope == "series")
            {
                if (ev.SeriesId is not int seriesId) { await component.UpdateAsync(m => { m.Content = "That isn't a series."; m.Components = Empty(); }); return; }
                await component.RespondWithModalAsync(BuildSeriesEditModal(seriesId, ev));
            }
            else
            {
                await component.RespondWithModalAsync(BuildEditModal("occ", ev.Id, ev, withWhen: false, withDuration: true, tz));
            }
        }
    }

    // ─── Modal submit ──────────────────────────────────────────────────────

    private async Task OnModalAsync(SocketModal modal)
    {
        if (!modal.Data.CustomId.StartsWith($"{Prefix}editsubmit:", StringComparison.Ordinal)) return;

        var parts = modal.Data.CustomId.Split(':'); // evtmgmt:editsubmit:<single|occ|series>:<id>
        if (parts.Length != 4 || !int.TryParse(parts[3], out var id)) { await modal.RespondAsync("Couldn't read that form.", ephemeral: true); return; }
        var scope = parts[2];

        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? string.Empty);
        var tz = await ResolveCallerZoneAsync(modal.User.Id);

        try
        {
            switch (scope)
            {
                case "single": await SubmitSingleEditAsync(modal, id, fields, tz, allowTimeChange: true);  break;
                case "occ":    await SubmitSingleEditAsync(modal, id, fields, tz, allowTimeChange: false); break;
                case "series": await SubmitSeriesEditAsync(modal, id, fields);                              break;
                default:       await modal.RespondAsync("Unknown edit type.", ephemeral: true);             break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Event edit submit failed ({Scope} {Id})", scope, id);
            try { await modal.RespondAsync("Something went wrong applying the edit.", ephemeral: true); } catch { }
        }
    }

    private async Task SubmitSingleEditAsync(
        SocketModal modal, int clanEventId, IReadOnlyDictionary<string, string> f, TimeZoneInfo tz, bool allowTimeChange)
    {
        var title = f.GetValueOrDefault("title", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) { await modal.RespondAsync("Title can't be empty.", ephemeral: true); return; }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId);
        if (ev is null || ev.Status == ClanEventStatus.Cancelled) { await modal.RespondAsync("That event is no longer available.", ephemeral: true); return; }
        if (!await CallerMayManageAsync(modal.User, ev.OrganizerId)) { await modal.RespondAsync("You don't have permission to edit that event.", ephemeral: true); return; }

        var newStart = ev.StartUtc;
        if (allowTimeChange)
        {
            var w = _time.ParseStart(f.GetValueOrDefault("when", ""), tz);
            if (!w.Success)        { await modal.RespondAsync($"Couldn't read the time: {w.Error}", ephemeral: true); return; }
            if (!w.HasTimeOfDay)   { await modal.RespondAsync("Please include a time of day.", ephemeral: true); return; }
            newStart = w.StartUtc;
        }

        var d = _time.ParseEnd(f.GetValueOrDefault("dur", ""), newStart, tz);
        if (!d.Success) { await modal.RespondAsync($"Couldn't read the duration/end: {d.Error}", ephemeral: true); return; }

        var timeMoved = ev.StartUtc != newStart;
        ev.Title       = title;
        ev.StartUtc    = newStart;
        ev.EndUtc      = d.EndUtc;
        ev.Description = f.GetValueOrDefault("desc", "").Trim();
        if (timeMoved) ev.RemindersSentCsv = string.Empty; // re-arm reminders for the new time

        await ApplyCalendarUpdateAsync(db, ev);
        await db.SaveChangesAsync();
        await UpdatePostAsync(db, ev);

        await modal.RespondAsync($"✅ Updated **{ev.Title}**.", ephemeral: true);
    }

    private async Task SubmitSeriesEditAsync(SocketModal modal, int seriesId, IReadOnlyDictionary<string, string> f)
    {
        var title = f.GetValueOrDefault("title", "").Trim();
        if (string.IsNullOrWhiteSpace(title)) { await modal.RespondAsync("Title can't be empty.", ephemeral: true); return; }
        var description = f.GetValueOrDefault("desc", "").Trim();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var series = await db.ClanEventSeries.FirstOrDefaultAsync(s => s.Id == seriesId);
        if (series is null) { await modal.RespondAsync("That series no longer exists.", ephemeral: true); return; }
        if (!await CallerMayManageAsync(modal.User, series.OrganizerId)) { await modal.RespondAsync("You don't have permission to edit that series.", ephemeral: true); return; }

        series.Title       = title;
        series.Description = description;

        var now = DateTime.UtcNow;
        var futures = await db.ClanEvents
            .Where(e => e.SeriesId == seriesId && e.Status == ClanEventStatus.Scheduled && e.StartUtc > now)
            .ToListAsync();

        foreach (var ev in futures)
        {
            ev.Title       = title;
            ev.Description = description;
            await ApplyCalendarUpdateAsync(db, ev);
        }
        await db.SaveChangesAsync();

        foreach (var ev in futures)
            await UpdatePostAsync(db, ev);

        await modal.RespondAsync(
            $"✅ Updated the series **{title}** and {futures.Count} upcoming occurrence{(futures.Count == 1 ? "" : "s")}.",
            ephemeral: true);
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
    private static async Task ApplyCalendarUpdateAsync(BotDbContext db, ClanEvent ev)
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
                Title       = ev.Title,
                StartUtc    = ev.StartUtc,
                EndUtc      = ev.EndUtc,
                Description = ev.Description,
                Source      = "Clan",
            }),
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt     = DateTime.UtcNow,
        });
    }

    /// <summary>Re-renders an event's #events post from current state (or shows it cancelled).</summary>
    private async Task UpdatePostAsync(BotDbContext db, ClanEvent ev)
    {
        try
        {
            if (_client.GetChannel(ev.ChannelId) is not IMessageChannel channel) return;
            if (await channel.GetMessageAsync(ev.MessageId) is not IUserMessage msg) return;

            var rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();
            var embed = EventEmbedBuilder.BuildEmbed(ev, rsvps);
            var comps = ev.Status == ClanEventStatus.Cancelled
                ? Empty()
                : EventEmbedBuilder.BuildComponents(ev.Id, _config.EventRsvpEnabled, locked: DateTime.UtcNow >= ev.StartUtc);

            await msg.ModifyAsync(m => { m.Embed = embed; m.Components = comps; });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-render post for event {ClanId} (msg {MsgId})", ev.Id, ev.MessageId);
        }
    }

    private Modal BuildEditModal(string scope, int clanEventId, ClanEvent ev, bool withWhen, bool withDuration, TimeZoneInfo tz)
    {
        var mb = new ModalBuilder()
            .WithTitle("Edit event")
            .WithCustomId($"{Prefix}editsubmit:{scope}:{clanEventId}")
            .AddTextInput("Title", "title", TextInputStyle.Short, value: ev.Title, required: true, maxLength: 100);

        if (withWhen)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(ev.StartUtc, tz);
            mb.AddTextInput("When", "when", TextInputStyle.Short,
                value: local.ToString("MMMM d, yyyy h:mm tt", CultureInfo.InvariantCulture), required: true);
        }
        if (withDuration)
        {
            var minutes = Math.Max(1, (int)Math.Round((ev.EndUtc - ev.StartUtc).TotalMinutes));
            mb.AddTextInput("Duration or end time", "dur", TextInputStyle.Short,
                value: $"{minutes} minutes", required: true);
        }

        mb.AddTextInput("Description", "desc", TextInputStyle.Paragraph,
            value: string.IsNullOrEmpty(ev.Description) ? null : ev.Description, required: false);
        return mb.Build();
    }

    private Modal BuildSeriesEditModal(int seriesId, ClanEvent ev)
    {
        return new ModalBuilder()
            .WithTitle("Edit series")
            .WithCustomId($"{Prefix}editsubmit:series:{seriesId}")
            .AddTextInput("Title", "title", TextInputStyle.Short, value: ev.Title, required: true, maxLength: 100)
            .AddTextInput("Description", "desc", TextInputStyle.Paragraph,
                value: string.IsNullOrEmpty(ev.Description) ? null : ev.Description, required: false)
            .Build();
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
