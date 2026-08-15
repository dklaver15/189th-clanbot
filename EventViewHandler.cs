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
/// /event view: look up the RSVP roster of an event that has already happened,
/// plus a comparison against who actually got attendance credit.
///
/// ── Why this exists ──
/// Every RSVP the bot has ever taken is still in the database. EventArchiveService
/// deletes only the #events post about EventArchiveDelayMinutes after the event
/// ends; it flips ClanEvent.Status to Archived and leaves both the ClanEvent row
/// and its EventRsvps rows alone, and nothing anywhere prunes them. What was
/// missing was any way to read them back: every other event picker in the bot
/// filters to Status == Scheduled AND StartUtc &gt; now, so a finished event
/// disappears from the interface entirely about an hour after it ends. Before
/// this command the only complete source was a sqlite3 query inside the running
/// container.
///
/// ── Two things in one reply ──
/// The first embed is the stored roster, rendered by the same
/// <see cref="EventEmbedBuilder.BuildEmbed"/> the live post uses, so it looks
/// exactly like the post did (minus the banner, which is not re-attached).
/// The second embed is the part the post never showed: RSVP against actual
/// voice presence, which is the only thing that earns credit. RSVP itself is
/// cosmetic (see <see cref="EventRsvp"/>), so "said Going" and "was credited"
/// are genuinely different sets and the gap between them is the interesting
/// number.
///
/// ── Matching an event to its attendance ──
/// EventAttendance / MeetingAttendance rows carry the CalendarEvents.Id they
/// were computed from, and <see cref="ClanEvent.CalendarEventId"/> is that same
/// id, so the join is direct. Two guards matter. A ClanEvent with
/// CalendarEventId == 0 is never matched, because 0 is also the sentinel
/// EventCreditCommandHandler stamps on manual /add-event-credit rows and
/// matching on it would pull in every hand-granted credit in the guild. And a
/// missing CalendarEvent row (cancelling an event deletes it) means attendance
/// was never snapshotted, which is reported as its own state rather than as
/// "nobody showed up".
///
/// ── Permissions ──
/// Gated on the HQ role (BotConfig.EventViewRoleId), not on a rank threshold.
/// This is a reporting view over who did and did not turn up after saying they
/// would, which is a narrower audience than the officers who can create events.
/// The autocomplete is gated too, so past event titles do not leak to members
/// who cannot run the command.
/// </summary>
public sealed class EventViewHandler
{
    /// <summary>Custom id prefix for this handler's own components. Distinct from
    /// EventManagementHandler's "evtmgmt:" so the two select handlers never
    /// see each other's interactions.</summary>
    private const string Prefix = "evtview:";

    /// <summary>The name of the optional slash option that carries an event id
    /// straight from autocomplete. Also the option this handler answers
    /// autocomplete for.</summary>
    public const string EventOptionName = "event";

    /// <summary>Discord's hard cap on both select-menu options and autocomplete
    /// choices.</summary>
    private const int MaxChoices = 25;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly ILogger<EventViewHandler> _logger;

    public EventViewHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        ILogger<EventViewHandler> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _logger   = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SelectMenuExecuted  += OnSelectAsync;
        client.AutocompleteExecuted += OnAutocompleteAsync;
    }

    // ─── Entry point (called by EventCommandHandler) ───────────────────────

    /// <summary>
    /// Runs the whole view off the gateway task. Resolving names can hit REST
    /// once per roster member who has left the guild, and Discord.NET awaits
    /// each gateway event handler before dispatching the next, so doing that
    /// inline would stall every other interaction for the duration. DeferAsync
    /// still runs first thing inside the task, well inside Discord's 3 second
    /// acknowledgement window.
    /// </summary>
    public Task StartViewAsync(SocketSlashCommand command)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunViewAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "/event view failed");
                try { await command.FollowupAsync("Something went wrong reading that event. Check the bot logs.", ephemeral: true); }
                catch { /* interaction already resolved or expired */ }
            }
        });
        return Task.CompletedTask;
    }

    private async Task RunViewAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }
        if (!await RequireViewRoleAsync(command)) return;

        // The "event" option is filled in by autocomplete, whose values are
        // ClanEvent ids. A member can also type free text and send it without
        // picking a suggestion, so an unparseable value falls through to the
        // picker, carrying whatever they typed through as a title filter rather
        // than being discarded.
        var raw = command.Data.Options.FirstOrDefault()?.Options
            ?.FirstOrDefault(o => o.Name == EventOptionName)?.Value as string;

        if (int.TryParse(raw, out var pickedId))
        {
            var view = await BuildViewAsync(command.GuildId.Value, pickedId);
            if (view.Error is not null)
            {
                await command.FollowupAsync(view.Error, ephemeral: true);
                return;
            }

            // Two messages, not two embeds on one message. Discord caps the
            // COMBINED length of a message's embeds at 6000 characters, and a
            // packed roster plus a packed attendance breakdown can clear that on
            // a big event, which would fail the whole reply.
            await command.FollowupAsync(view.Header, embeds: new[] { view.Roster! }, ephemeral: true);
            await command.FollowupAsync(embeds: new[] { view.Diff! }, ephemeral: true);
            return;
        }

        await ShowPickerAsync(command, raw);
    }

    // ─── Picker ────────────────────────────────────────────────────────────

    private async Task ShowPickerAsync(SocketSlashCommand command, string? titleFilter)
    {
        var events = await LoadPastEventsAsync(command.GuildId!.Value, titleFilter);

        if (events.Count == 0)
        {
            await command.FollowupAsync(
                string.IsNullOrWhiteSpace(titleFilter)
                    ? "No finished events on record yet."
                    : $"No finished event matches \"{titleFilter}\". Run it with no name to see the most recent ones.",
                ephemeral: true,
                allowedMentions: AllowedMentions.None);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}pick")
            .WithPlaceholder("Choose a past event")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var ev in events)
        {
            var note = ev.Status == ClanEventStatus.Cancelled ? "cancelled, " : string.Empty;
            menu.AddOption(
                // A select option label must be at least one character, so an
                // event that somehow has a blank title cannot be allowed to
                // throw and take the whole picker down with it.
                Truncate(TitleOf(ev), 100),
                ev.Id.ToString(),
                Truncate($"{note}{ev.StartUtc:ddd d MMM yyyy} UTC", 100));
        }

        await command.FollowupAsync(
            "Which event?",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    private async Task OnSelectAsync(SocketMessageComponent component)
    {
        if (component.Data.CustomId != $"{Prefix}pick") return;

        try
        {
            // UpdateAsync, not DeferAsync: it acknowledges and edits in one call,
            // which STRIPS THE MENU immediately. A plain deferred update leaves
            // the select live while the lookup runs, and building a view can take
            // seconds (a REST lookup per departed roster member). A second pick
            // during that window would race the first: both would edit the same
            // roster message and both would post a breakdown, leaving a roster
            // from one event sitting above a breakdown from another.
            await component.UpdateAsync(m =>
            {
                m.Content    = "Looking that up.";
                m.Embeds     = Array.Empty<Embed>();
                m.Components = new ComponentBuilder().Build();
            });

            if (component.GuildId is null) return;

            // Re-check the gate on the click, not just on the command. The picker
            // is ephemeral so only the invoker can click it, but the role may have
            // come off them since it was opened.
            if (component.User is not SocketGuildUser gu || !HasViewRole(gu))
            {
                await ReplaceAsync(component, "You no longer have permission to look up past events.");
                return;
            }

            if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var clanEventId))
            {
                await ReplaceAsync(component, "Something went wrong reading that selection.");
                return;
            }

            var view = await BuildViewAsync(component.GuildId.Value, clanEventId);
            if (view.Error is not null)
            {
                await ReplaceAsync(component, view.Error);
                return;
            }

            // The roster replaces the picker in place; the breakdown arrives as a
            // second ephemeral, for the 6000-character reason in RunViewAsync.
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content    = view.Header;
                m.Embeds     = new[] { view.Roster! };
                m.Components = new ComponentBuilder().Build();
            });
            await component.FollowupAsync(embeds: new[] { view.Diff! }, ephemeral: true);
        }
        catch (Exception ex)
        {
            // Without this the interaction is left acknowledged but unchanged:
            // the member sees "Looking that up." forever and never learns it
            // failed.
            _logger.LogError(ex, "/event view selection failed");
            try { await ReplaceAsync(component, "Something went wrong reading that event. Check the bot logs."); }
            catch { /* the interaction token has expired, nothing left to say */ }
        }
    }

    private static Task ReplaceAsync(SocketMessageComponent component, string text) =>
        component.ModifyOriginalResponseAsync(m =>
        {
            m.Content    = text;
            m.Embeds     = Array.Empty<Embed>();
            m.Components = new ComponentBuilder().Build();
        });

    // ─── Autocomplete ──────────────────────────────────────────────────────

    /// <summary>
    /// Answers the "event" option on /event view with matching past events,
    /// newest first. Fires for every autocomplete interaction in the guild, so
    /// it filters hard and returns an empty list (never an error) for anything
    /// that is not ours: Discord shows "no options match" and the member can
    /// still type freely.
    ///
    /// This one runs inline on the gateway task rather than being offloaded like
    /// StartViewAsync, because an autocomplete has to be answered inside three
    /// seconds and the work is a single indexed SQLite read. Do not grow it into
    /// anything that touches the network.
    /// </summary>
    private async Task OnAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        try
        {
            if (interaction.Data.CommandName != EventCommandHandler.EventCommandName) return;
            if (interaction.Data.Current?.Name != EventOptionName) return;

            if (interaction.GuildId is null || interaction.User is not SocketGuildUser user || !HasViewRole(user, quiet: true))
            {
                await interaction.RespondAsync(Array.Empty<AutocompleteResult>());
                return;
            }

            var typed  = interaction.Data.Current.Value as string;
            var events = await LoadPastEventsAsync(interaction.GuildId.Value, typed);

            var results = events.Select(ev => new AutocompleteResult(
                Truncate($"{TitleOf(ev)} ({ev.StartUtc:d MMM yyyy})", 100),
                ev.Id.ToString()));

            await interaction.RespondAsync(results);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "/event view autocomplete failed");
            try { await interaction.RespondAsync(Array.Empty<AutocompleteResult>()); }
            catch { /* already answered, or the interaction expired */ }
        }
    }

    /// <summary>
    /// Events whose end time has passed, newest first, capped at Discord's
    /// 25-choice limit.
    ///
    /// Keyed on EndUtc rather than on Status == Archived on purpose: an event is
    /// finished the moment it ends, but EventArchiveService does not stamp
    /// Archived until EventArchiveDelayMinutes later. Reading EndUtc means the
    /// roster is available the second the event is over, including during the
    /// window where the post is still up. Cancelled events are included too:
    /// their RSVPs are still history, and the picker labels them.
    /// </summary>
    private async Task<List<ClanEvent>> LoadPastEventsAsync(ulong guildId, string? titleFilter)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now   = DateTime.UtcNow;
        var query = db.ClanEvents.Where(e => e.GuildId == guildId && e.EndUtc <= now);

        if (!string.IsNullOrWhiteSpace(titleFilter))
        {
            // EF.Functions.Like, NOT string.Contains. EF Core 8 translates
            // Contains on SQLite to instr(), which is byte-comparison and
            // therefore case-SENSITIVE, so typing "raid" would not find "Raid
            // Night". SQLite's LIKE is case-insensitive for ASCII, which is what
            // someone typing into an autocomplete box expects. The wildcards are
            // ours, so the user's own % and _ are escaped first to stay literal.
            var f = titleFilter.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            query = query.Where(e => EF.Functions.Like(e.Title, $"%{f}%", "\\"));
        }

        return await query.OrderByDescending(e => e.StartUtc).Take(MaxChoices).ToListAsync();
    }

    // ─── Render ────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything one lookup produces: a lead line, the stored roster, and the
    /// attendance breakdown. Error is set instead of the rest when the event
    /// can't be read at all, so both entry points can report it in whichever way
    /// suits their interaction without duplicating the query.
    /// </summary>
    private sealed record ViewResult(string Header, Embed? Roster, Embed? Diff, string? Error);

    private async Task<ViewResult> BuildViewAsync(ulong guildId, int clanEventId)
    {
        ClanEvent? ev;
        List<EventRsvp> rsvps;
        List<AttendedMember> attended;
        CalendarEvent? cal;

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            ev = await db.ClanEvents.FirstOrDefaultAsync(e => e.Id == clanEventId && e.GuildId == guildId);
            if (ev is null)
                return new ViewResult(string.Empty, null, null, "I can't find that event.");

            // Typing a raw number into the option skips autocomplete entirely, so
            // the "past events only" rule has to be enforced here as well as in
            // the lookup. Rendering an upcoming event under a "when the event
            // finished" header would be quietly wrong.
            if (ev.EndUtc > DateTime.UtcNow)
                return new ViewResult(string.Empty, null, null,
                    $"**{TitleOf(ev)}** hasn't finished yet, so there's nothing to look back at. Its post in the events channel has the live roster.");

            rsvps = await db.EventRsvps.Where(r => r.ClanEventId == ev.Id).ToListAsync();

            // CalendarEventId 0 is the manual-credit sentinel, never a real
            // event, so it must not be used as a join key. See the class remarks.
            if (ev.CalendarEventId == 0)
            {
                cal      = null;
                attended = new List<AttendedMember>();
            }
            else
            {
                cal = await db.CalendarEvents.FirstOrDefaultAsync(c => c.Id == ev.CalendarEventId);

                var eventRows = await db.EventAttendances
                    .Where(a => a.GuildId == guildId && a.CalendarEventId == ev.CalendarEventId)
                    .Select(a => new { a.UserId, a.Username, a.AttendedMinutes })
                    .ToListAsync();

                // Meetings are folded in for the same reason EventAttendanceHelper
                // folds them in: by clan policy a meeting counts as an event, and
                // the two snapshot pipelines write to separate tables against the
                // same CalendarEvent. Someone credited by either one turned up.
                var meetingRows = await db.MeetingAttendances
                    .Where(a => a.GuildId == guildId && a.CalendarEventId == ev.CalendarEventId)
                    .Select(a => new { a.UserId, a.Username, a.AttendedMinutes })
                    .ToListAsync();

                attended = eventRows.Concat(meetingRows)
                    .GroupBy(a => a.UserId)
                    .Select(g => new AttendedMember(
                        g.Key,
                        g.Select(x => x.Username).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty,
                        g.Max(x => x.AttendedMinutes)))
                    .ToList();
            }
        }

        var ids = rsvps.Select(r => r.UserId)
            .Concat(attended.Select(a => a.UserId))
            .Append(ev.HostId ?? ev.OrganizerId)
            .Append(ev.OrganizerId);
        var resolve = await EventEmbedBuilder.GuildNameResolverAsync(_client, ev.GuildId, ids);

        var roster = EventEmbedBuilder.BuildEmbed(ev, rsvps, imageFileName: null, resolveName: resolve);
        var diff   = BuildAttendanceEmbed(ev, cal, rsvps, attended, resolve);

        var header = ev.Status == ClanEventStatus.Cancelled
            ? $"**{TitleOf(ev)}** was cancelled. This is the roster as it stood at the time."
            : $"Roster for **{TitleOf(ev)}**, as it stood when the event finished.";

        return new ViewResult(header, roster, diff, null);
    }

    private sealed record AttendedMember(ulong UserId, string Username, int Minutes);

    /// <summary>
    /// The RSVP-against-reality embed. Every "who did not show" style question
    /// has to state its own caveats, because attendance credit is not a door
    /// count: it is voice presence in the events channel past a minutes
    /// threshold during the buffered event window. Someone who dropped in for
    /// five minutes reads the same as someone who never came.
    /// </summary>
    private Embed BuildAttendanceEmbed(
        ClanEvent ev,
        CalendarEvent? cal,
        IReadOnlyCollection<EventRsvp> rsvps,
        IReadOnlyCollection<AttendedMember> attended,
        Func<ulong, string?> resolve)
    {
        var eb = new EmbedBuilder()
            .WithTitle("Who actually turned up")
            .WithColor(new Color(0x2B2D31));

        // Each "nothing to show" case only applies when there is genuinely
        // nothing to show. Attendance rows are never deleted by any lifecycle
        // event, so an event that ran and was cancelled AFTERWARDS (to tidy the
        // calendar) still has real attendance sitting in the table, and claiming
        // otherwise would hide credit that already counts toward promotion.
        if (attended.Count == 0)
        {
            if (ev.Status == ClanEventStatus.Cancelled)
            {
                eb.WithDescription("This event was cancelled and nobody was credited for it.");
                return eb.Build();
            }
            if (ev.CalendarEventId == 0 || cal is null)
            {
                eb.WithDescription(
                    "No calendar entry survives for this event, so its attendance can't be matched up. " +
                    "The RSVP roster above is still complete.");
                return eb.Build();
            }

            // Both snapshot pipelines have their own stamp and their own timer.
            // Only report "not worked out yet" when NEITHER has run, otherwise a
            // lookup in the gap between the two sweeps reports a false pending.
            if (cal.LastSnapshotAttemptUtc is null && cal.LastMeetingSnapshotAttemptUtc is null)
            {
                eb.WithDescription(
                    "Attendance hasn't been worked out for this event yet. The snapshot runs shortly after an " +
                    "event ends, so check back in a few minutes. If it stays this way, the event ended before " +
                    "attendance tracking picked it up.");
                return eb.Build();
            }
        }

        // Signup order, matching how the Going roster renders on the post.
        var goingOrdered = rsvps.Where(r => r.Status == EventRsvpStatus.Going)
            .OrderBy(r => r.UpdatedAt)
            .Select(r => r.UserId)
            .ToList();
        var going  = goingOrdered.ToHashSet();
        var byUser = rsvps.ToDictionary(r => r.UserId, r => r.Status);
        var showed = attended.ToDictionary(a => a.UserId, a => a);

        var kept   = goingOrdered.Where(showed.ContainsKey).ToList();
        var noShow = goingOrdered.Where(id => !showed.ContainsKey(id)).ToList();
        var extra  = attended.Where(a => !going.Contains(a.UserId)).OrderByDescending(a => a.Minutes).ToList();

        var lead = going.Count == 0
            ? $"Nobody RSVP'd Going. {attended.Count} member(s) were credited for being there."
            : $"{kept.Count} of {going.Count} who said Going were credited. {attended.Count} member(s) were credited in total.";

        // Waitlisters said yes too, they just never got a confirmed spot, so a
        // waitlister who did not turn up belongs in neither "said Going" bucket
        // and would otherwise vanish from the report entirely.
        var waitlisted = rsvps.Where(r => r.Status == EventRsvpStatus.Waitlisted).Select(r => r.UserId).ToList();
        var waitNoShow = waitlisted.Count(id => !showed.ContainsKey(id));
        if (waitlisted.Count > 0)
            lead += $"\n{waitlisted.Count} were waitlisted, {waitNoShow} of them not credited.";

        eb.WithDescription(lead);

        eb.AddField($"✅ Said Going, credited ({kept.Count})",
            NameBlock(kept.Select(id => Label(id, resolve, showed))), inline: false);

        // "No credit", not "no show". Attendance credit is a minutes threshold on
        // voice presence, and /add-event-credit rows are invisible to this query
        // (they carry the sentinel CalendarEventId 0), so this list is the people
        // to look INTO, not a list of people who definitely weren't there.
        eb.AddField($"⚠️ Said Going, no credit ({noShow.Count})",
            NameBlock(noShow.Select(id => Label(id, resolve, showed))), inline: false);

        eb.AddField($"➕ Credited without a Going RSVP ({extra.Count})",
            NameBlock(extra.Select(a =>
            {
                var said = byUser.TryGetValue(a.UserId, out var s)
                    ? s switch
                    {
                        EventRsvpStatus.Maybe      => "said Maybe",
                        EventRsvpStatus.Decline    => "declined",
                        EventRsvpStatus.Waitlisted => "was waitlisted",
                        _                          => "no RSVP",
                    }
                    : "no RSVP";
                return $"{Label(a.UserId, resolve, showed)} ({said})";
            })), inline: false);

        // Both thresholds are named because both pipelines feed this embed, and
        // quoting only the events one would contradict a meeting-credited member
        // whose minutes are printed right above it. Read from config rather than
        // written in, so the text can't drift from the rule.
        eb.WithFooter(
            $"Credit means at least {_config.AutoPromotionMinEventAttendanceMinutes} minutes in the events voice " +
            $"channel around the event, or {_config.AutoPromotionMinMeetingAttendanceMinutes} in the meetings one. " +
            "Someone who only dropped in briefly counts as no credit, and credit added by hand with " +
            "/add-event-credit does not show here.");

        return eb.Build();
    }

    /// <summary>
    /// One roster line: the live display name where we can resolve it, otherwise
    /// the name snapshotted onto the attendance row at the time (which survives
    /// the member leaving), otherwise a raw mention. Minutes are appended for
    /// anyone who was credited, since "31 minutes" and "3 hours" are very
    /// different answers to "did they actually take part".
    /// </summary>
    private static string Label(ulong userId, Func<ulong, string?> resolve, IReadOnlyDictionary<ulong, AttendedMember> showed)
    {
        var name = resolve(userId);
        if (string.IsNullOrWhiteSpace(name) && showed.TryGetValue(userId, out var row) && !string.IsNullOrWhiteSpace(row.Username))
            name = row.Username;

        var label = string.IsNullOrWhiteSpace(name) ? $"<@{userId}>" : Escape(name);

        return showed.TryGetValue(userId, out var a) ? $"{label} ({a.Minutes} min)" : label;
    }

    /// <summary>
    /// Blockquoted one-per-line list, matching the roster fields on the event
    /// post, cut at a line boundary to stay inside the 1024 character embed
    /// field limit.
    /// </summary>
    private static string NameBlock(IEnumerable<string> labels)
    {
        var list = labels.ToList();
        if (list.Count == 0) return "—";

        var lines = new List<string>();
        var len   = 0;
        var shown = 0;
        foreach (var label in list)
        {
            var add = label.Length + 3;
            if (len + add > 980) break;
            lines.Add($"> {label}");
            len += add;
            shown++;
        }
        if (shown < list.Count)
            lines.Add($"> …and {list.Count - shown} more");

        return string.Join("\n", lines);
    }

    /// <summary>Escapes inline markdown plus '[' so a hostile nickname can't
    /// render as a masked link. Mirrors EventEmbedBuilder.EscapePlain.</summary>
    private static string Escape(string s) => s
        .Replace("\\", "\\\\")
        .Replace("[", "\\[")
        .Replace("*", "\\*")
        .Replace("_", "\\_")
        .Replace("~", "\\~")
        .Replace("`", "\\`")
        .Replace("|", "\\|");

    /// <summary>An event's title, never blank. Select-menu labels and
    /// autocomplete choice names both reject the empty string.</summary>
    private static string TitleOf(ClanEvent ev) =>
        string.IsNullOrWhiteSpace(ev.Title) ? "(untitled event)" : ev.Title;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");

    // ─── Permission gate ───────────────────────────────────────────────────

    /// <summary>
    /// Fails closed in both misconfigured directions, matching how /xp-adjust
    /// gates on its role: an id naming a role that no longer exists denies
    /// everyone except Administrators and says so in the log, while an id of 0
    /// means "not configured" and hands back to the ordinary /event rank gate so
    /// an upgrade that drops the key does not lock the command out entirely.
    /// </summary>
    private async Task<bool> RequireViewRoleAsync(SocketSlashCommand command)
    {
        if (command.User is not SocketGuildUser user)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return false;
        }
        if (HasViewRole(user)) return true;

        if (_config.EventViewRoleId != 0 && user.Guild.GetRole(_config.EventViewRoleId) is null)
        {
            _logger.LogError(
                "EventViewRoleId {Role} does not exist in guild {Guild}. /event view is denied to everyone " +
                "except Administrators until it is corrected",
                _config.EventViewRoleId, user.Guild.Id);
        }

        var who = _config.EventViewRoleId != 0
            ? $"<@&{_config.EventViewRoleId}>"
            : $"**{_config.EventCommandMinRank} and above**";
        await command.FollowupAsync(
            $"❌ Looking up past event rosters is restricted to {who}.",
            ephemeral: true,
            allowedMentions: AllowedMentions.None);
        return false;
    }

    /// <param name="quiet">Suppresses the misconfiguration warning. The
    /// autocomplete path calls this on every keystroke, and an unset role id
    /// would otherwise fill the log with one line per character typed.</param>
    private bool HasViewRole(SocketGuildUser user, bool quiet = false)
    {
        // An Administrator can hand themselves the role in about four seconds, so
        // gating them out only makes the check look stronger than it is. Same
        // reasoning as /xp-adjust and /ticket-panel.
        if (user.GuildPermissions.Administrator) return true;

        if (_config.EventViewRoleId == 0)
        {
            if (!quiet)
                _logger.LogWarning("EventViewRoleId is not set. Falling back to the EventCommandMinRank gate for /event view");
            return HasEventRank(user);
        }

        return user.Roles.Any(r => r.Id == _config.EventViewRoleId);
    }

    /// <summary>
    /// SyncWithHandlers: EventCommandHandler.HasEventPermission. Only reached
    /// when EventViewRoleId is 0.
    /// </summary>
    private bool HasEventRank(SocketGuildUser user)
    {
        if (user.GuildPermissions.ManageRoles) return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(_config.EventCommandMinRank);
        if (minIndex < 0) return false;

        var highest = user.Roles.Select(r => rankRoles.IndexOf(r.Name)).DefaultIfEmpty(-1).Max();
        return highest >= minIndex;
    }
}
