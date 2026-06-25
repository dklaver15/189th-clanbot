using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ClanGuardBot.Handlers;

/// <summary>
/// The DM event-creation wizard — Apollo-style. Triggered by /event, it walks
/// the organizer through title → when → duration → description → recurrence →
/// confirm in their DMs, then hands the validated draft to
/// <see cref="IEventPublisher"/>.
///
/// ── Routing ──
/// Self-registers MessageReceived (DM text replies) and ButtonExecuted (the
/// timezone/recurrence/confirm buttons, all prefixed "evtwiz:"). Both filter to
/// the user's active session, so this coexists with every other handler on
/// those events.
///
/// ── Sessions ──
/// In-memory, keyed by organizer id, expired after <see cref="IdleTimeout"/> of
/// inactivity and removed on completion/cancel. Restarts drop in-progress
/// sessions; the organizer just re-runs /event.
///
/// ── Times ──
/// All parsing routes through <see cref="EventTimeParser"/> in the organizer's
/// zone; the draft carries UTC. Display uses Discord &lt;t:unix&gt; markdown so
/// each preview/echo localizes to the viewer.
/// </summary>
public sealed class EventCreationWizard
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private const string Prefix = "evtwiz:";

    private readonly ConcurrentDictionary<ulong, EventCreationSession> _sessions = new();

    // Shared client for pulling the organizer's uploaded image off the Discord CDN.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly IServiceProvider _services;
    private readonly ILogger<EventCreationWizard> _logger;
    private readonly BotConfig _config;
    private readonly EventTimeParser _time;
    private readonly IEventPublisher _publisher;
    private readonly System.Threading.Timer _idleSweep;

    public EventCreationWizard(
        IServiceProvider services,
        ILogger<EventCreationWizard> logger,
        IOptions<BotConfig> config,
        EventTimeParser time,
        IEventPublisher publisher)
    {
        _services  = services;
        _logger    = logger;
        _config    = config.Value;
        _time      = time;
        _publisher = publisher;

        // Proactively time out abandoned DM sessions: every minute, DM anyone
        // who's gone idle past IdleTimeout and drop their session.
        _idleSweep = new System.Threading.Timer(_ => _ = SweepIdleAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceivedAsync;
        client.ButtonExecuted  += OnButtonExecutedAsync;
    }

    // ─── Entry point (called by EventCommandHandler) ───────────────────────

    /// <summary>
    /// Opens a DM and starts the wizard. Returns false if the user's DMs are
    /// closed (the command handler surfaces that to the caller).
    /// </summary>
    public async Task<bool> StartAsync(IUser user, ulong guildId)
    {
        PruneExpired();

        IDMChannel dm;
        try
        {
            dm = await user.CreateDMChannelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not open DM with {User} for /event", user.Id);
            return false;
        }

        string? tz;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            tz = (await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == user.Id))?.IanaId;
        }

        var session = new EventCreationSession
        {
            Dm             = dm,
            StartedAt      = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            TimezoneKnown  = !string.IsNullOrWhiteSpace(tz),
            Draft = new EventDraft
            {
                GuildId       = guildId,
                OrganizerId   = user.Id,
                OrganizerName = (user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username,
                TimeZoneId    = tz ?? string.Empty,
            },
        };
        _sessions[user.Id] = session;

        try
        {
            // Ask for a timezone only when the organizer hasn't set one. With a
            // known zone we skip straight to the title (fast path, asked just
            // once ever); without one we must NOT silently assume
            // EventDefaultTimeZone — that's exactly how an Eastern organizer
            // typing "8pm" ended up an hour off (read as Central). Their pick is
            // saved as a personal default, so the question never repeats. Both the
            // button and typed paths advance to the Title step.
            if (session.TimezoneKnown)
            {
                session.Step = WizardStep.Title;
                await dm.SendMessageAsync(embed: Form("🎯 New event", "What's the **title** of the event?"));
            }
            else
            {
                session.Step = WizardStep.Timezone;
                await PromptTimezoneAsync(session);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening DM to {User} for /event", user.Id);
            _sessions.TryRemove(user.Id, out _);
            return false;
        }
    }

    // ─── DM text replies ───────────────────────────────────────────────────

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_sessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsExpired(s))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSendEmbed(s, Form("⌛ Event setup timed out",
                "That setup expired from inactivity and **nothing was created**. Run `/event` to start over."));
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSendEmbed(s, Form("Cancelled", "No event was created. Run `/event` to start again anytime."));
            return;
        }

        try
        {
            switch (s.Step)
            {
                case WizardStep.Timezone:        await HandleTimezoneTextAsync(s, text);   break;
                case WizardStep.Title:           await HandleTitleAsync(s, text);          break;
                case WizardStep.When:            await HandleWhenAsync(s, text);           break;
                case WizardStep.Duration:        await HandleDurationAsync(s, text);       break;
                case WizardStep.Description:     await HandleDescriptionAsync(s, text);    break;
                case WizardStep.Image:           await HandleImageAsync(s, message, text);  break;
                case WizardStep.MaxParticipants: await HandleMaxParticipantsAsync(s, text);  break;
                case WizardStep.RecurrenceUntil: await HandleRecurrenceUntilAsync(s, text); break;
                case WizardStep.SpecificDates:   await HandleSpecificDatesAsync(s, text);  break;
                // Recurrence / Confirm are button steps — ignore stray text.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wizard text step {Step} failed for {User}", s.Step, message.Author.Id);
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "Something went wrong. Run `/event` to start over.");
        }
    }

    private async Task HandleTimezoneTextAsync(EventCreationSession s, string text)
    {
        if (!_time.TryResolveTimeZone(text, out _, out var iana))
        {
            await s.Dm.SendMessageAsync(
                "I didn't recognize that timezone. Try `Central` / `CST`, or an IANA id like `America/Chicago` — or tap a button above.");
            return;
        }

        await SaveTimeZoneAsync(s.Draft.OrganizerId, iana);
        s.Draft.TimeZoneId = iana;
        s.Step = WizardStep.Title;
        await s.Dm.SendMessageAsync($"Timezone set to **{iana}**.\n\n🎯 What's the **title** of the event?");
    }

    private async Task HandleTitleAsync(EventCreationSession s, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Title", "Give the event a title — e.g. `Friday Night Ops`."));
            return;
        }
        if (text.Length > 100)
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Title too long", "Keep it under 100 characters — try a shorter one."));
            return;
        }

        s.Draft.Title = text;
        s.Step = WizardStep.When;
        await s.Dm.SendMessageAsync(embed: Form("🕒 When is it?", "Say things like `7pm tomorrow`, `in 2 hours`, or `June 14 at 8pm`."));
    }

    private async Task HandleWhenAsync(EventCreationSession s, string text)
    {
        var tz = _time.ResolveZone(s.Draft.TimeZoneId);
        var r  = _time.ParseStart(text, tz);

        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(embed: Form("Let's try that again", r.Error));
            return;
        }
        if (!r.HasTimeOfDay)
        {
            await s.Dm.SendMessageAsync(embed: Form("🕒 Need a time of day", "I got a date but no time — include a time too, like `June 14 at 7pm`."));
            return;
        }

        s.Draft.StartUtc = r.StartUtc;

        var echo = $"📅 Start: {EventTimeParser.Stamp(r.StartUtc, 'F')} ({EventTimeParser.Stamp(r.StartUtc, 'R')})";
        if (r.WasAmbiguous)
            echo += "\n*(I picked the soonest matching time — you can double-check at the end.)*";

        if (r.EndUtc is DateTime end)
        {
            // A range was given ("7-9pm tomorrow") — skip the duration step.
            s.Draft.EndUtc = end;
            await s.Dm.SendMessageAsync(embed: Form("🕒 Start & end set", $"{echo}\n⏱️ Ends: {EventTimeParser.Stamp(end, 't')}"));
            await AdvanceToDescriptionAsync(s);
        }
        else
        {
            s.Step = WizardStep.Duration;
            await s.Dm.SendMessageAsync(embed: Form("⏱️ How long does it run?", $"{echo}\n\ne.g. `2 hours`, `90 minutes`, or `until 9pm`."));
        }
    }

    private async Task HandleDurationAsync(EventCreationSession s, string text)
    {
        var tz = _time.ResolveZone(s.Draft.TimeZoneId);
        var r  = _time.ParseEnd(text, s.Draft.StartUtc, tz);

        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(embed: Form("Let's try that again", r.Error));
            return;
        }

        s.Draft.EndUtc = r.EndUtc;
        await AdvanceToDescriptionAsync(s);
    }

    private async Task AdvanceToDescriptionAsync(EventCreationSession s)
    {
        s.Step = WizardStep.Description;
        await s.Dm.SendMessageAsync(embed: Form("📝 Description", "Add a short description for the event, or type `skip`."));
    }

    private async Task HandleDescriptionAsync(EventCreationSession s, string text)
    {
        // Cap at Discord's embed-description limit (4096). The live #events post
        // renders this via WithDescription (4096 max); without this guard an
        // over-long paste blows up at publish time. The confirm preview truncates
        // further (to 1024) because it shows the description as an embed field.
        if (text.Length > 4096
         && !text.Equals("skip", StringComparison.OrdinalIgnoreCase)
         && !text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Description too long",
                "Keep it under 4096 characters — trim it down, or type `skip`."));
            return;
        }

        s.Draft.Description = text.Equals("skip", StringComparison.OrdinalIgnoreCase)
                           || text.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : text;

        await PromptImageAsync(s);
    }

    private async Task PromptImageAsync(EventCreationSession s)
    {
        s.Step = WizardStep.Image;
        await s.Dm.SendMessageAsync(embed: Form("🖼️ Event image", "Drag an image into this DM, or paste a GIF/image link (Tenor, Giphy, or direct — PNG/JPG/GIF/WebP, max 8 MB). Or type `skip`."));
    }

    private async Task HandleImageAsync(EventCreationSession s, SocketMessage message, string text)
    {
        if (text.Equals("skip", StringComparison.OrdinalIgnoreCase)
         || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            s.Draft.ImageBytes    = null;
            s.Draft.ImageFileName = null;
            s.Step = WizardStep.MaxParticipants;
            await PromptMaxParticipantsAsync(s);
            return;
        }

        var att = message.Attachments.FirstOrDefault();
        if (att is null)
        {
            // No file dragged in — maybe they pasted a GIF/image link.
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await s.Dm.SendMessageAsync("⏳ Fetching that image…");
                var res = await EventImageFetcher.FromUrlAsync(text);
                if (!res.Ok)
                {
                    await s.Dm.SendMessageAsync(embed: Form("🖼️ Couldn't use that link", $"{res.Error} Try another link, drag the file in, or type `skip`."));
                    return;
                }

                s.Draft.ImageFileName = res.FileName;
                s.Draft.ImageBytes    = EventImage.Downscale(res.Bytes!, res.FileName);
                s.Step = WizardStep.MaxParticipants;
                await PromptMaxParticipantsAsync(s);
                return;
            }

            await s.Dm.SendMessageAsync(embed: Form("🖼️ Event image", "Drag an image into this DM, paste a GIF/image link, or type `skip`."));
            return;
        }

        var looksImage = (att.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                      || EventImage.IsAllowedExtension(att.Filename);
        if (!looksImage)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Not an image", "That doesn't look like a PNG/JPG/GIF/WebP. Try another, or type `skip`."));
            return;
        }
        if (att.Size > EventImage.MaxBytes)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image too large", $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB — try a smaller one, or type `skip`."));
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await Http.GetByteArrayAsync(att.Url);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download event image for {User}", s.Draft.OrganizerId);
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Download failed", "Couldn't download that image. Try again, or type `skip`."));
            return;
        }
        if (bytes.Length > EventImage.MaxBytes)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image too large", $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB — try a smaller one, or type `skip`."));
            return;
        }

        var imgName = EventImage.Sanitize(att.Filename);
        s.Draft.ImageFileName = imgName;
        s.Draft.ImageBytes    = EventImage.Downscale(bytes, imgName);
        s.Step = WizardStep.MaxParticipants;
        await PromptMaxParticipantsAsync(s);
    }

    private async Task PromptMaxParticipantsAsync(EventCreationSession s)
    {
        await s.Dm.SendMessageAsync(embed: Form(
            "👥 Limit the number of attendees?",
            "Type a **max number** of people who can be on the Going list — extra " +
            "sign-ups go on a waitlist and move up automatically when a spot opens.\n\n" +
            "Type `none` (or `0`) for no limit. You can change this anytime after the event is created."));
    }

    private async Task HandleMaxParticipantsAsync(EventCreationSession s, string text)
    {
        text = text.Trim();

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)
         || text.Equals("skip", StringComparison.OrdinalIgnoreCase)
         || text.Equals("unlimited", StringComparison.OrdinalIgnoreCase)
         || text == "0")
        {
            s.Draft.MaxParticipants = null;
        }
        else if (int.TryParse(text, out var n) && n > 0)
        {
            s.Draft.MaxParticipants = n;
        }
        else
        {
            await s.Dm.SendMessageAsync(embed: Form(
                "👥 Need a number",
                "Give me a whole number greater than 0 (e.g. `16`), or type `none` for no limit."));
            return;
        }

        s.Step = WizardStep.Recurrence;
        await PromptRecurrenceAsync(s);
    }

    private async Task HandleRecurrenceUntilAsync(EventCreationSession s, string text)
    {
        var lower = text.Trim().ToLowerInvariant();

        if (lower is "none" or "forever" or "open" or "ongoing")
        {
            s.Draft.UntilUtc = null;
            s.Draft.MaxOccurrences = null;
        }
        else if (int.TryParse(lower, out var count) && count > 0)
        {
            s.Draft.MaxOccurrences = count;
            s.Draft.UntilUtc = null;
        }
        else
        {
            var tz = _time.ResolveZone(s.Draft.TimeZoneId);
            var r  = _time.ParseStart(text, tz);
            if (!r.Success)
            {
                await s.Dm.SendMessageAsync(embed: Form("🔁 Until when?", "Tell me an **end date** (`August 1`), a **number of times** (`8`), or `none` for open-ended."));
                return;
            }
            s.Draft.UntilUtc = r.StartUtc;
            s.Draft.MaxOccurrences = null;
        }

        await PromptConfirmAsync(s);
    }

    private async Task HandleSpecificDatesAsync(EventCreationSession s, string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        if (lower is "done" or "finish" or "end")
        {
            if (s.Draft.SpecificDatesUtc.Count == 0)
            {
                await s.Dm.SendMessageAsync(embed: Form("🗓️ Add at least one more",
                    "So far there's only the first date. Add another date & time, or type `cancel` to start over as a single event."));
                return;
            }
            await PromptConfirmAsync(s);
            return;
        }

        var tz = _time.ResolveZone(s.Draft.TimeZoneId);
        var r  = _time.ParseStart(text, tz);
        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(embed: Form("Let's try that again",
                string.IsNullOrWhiteSpace(r.Error) ? "I couldn't read that date — try `Thursday 8pm` or `June 20 at 7pm`." : r.Error));
            return;
        }
        if (!r.HasTimeOfDay)
        {
            await s.Dm.SendMessageAsync(embed: Form("🕒 Need a time of day", "Include a time too, like `Thursday at 8pm`."));
            return;
        }
        if (r.StartUtc <= DateTime.UtcNow)
        {
            await s.Dm.SendMessageAsync(embed: Form("🗓️ That's in the past", "Pick a future date & time."));
            return;
        }
        if (r.StartUtc == s.Draft.StartUtc || s.Draft.SpecificDatesUtc.Contains(r.StartUtc))
        {
            await s.Dm.SendMessageAsync(embed: Form("🗓️ Already on the list", "That date's already added. Send a different one, or type `done`."));
            return;
        }

        s.Draft.SpecificDatesUtc.Add(r.StartUtc);
        s.Draft.SpecificDatesUtc.Sort();
        var total = 1 + s.Draft.SpecificDatesUtc.Count;
        await s.Dm.SendMessageAsync(embed: Form("🗓️ Date added",
            $"Added **{EventTimeParser.Stamp(r.StartUtc, 'F')}** — **{total}** dates so far.\n\nAdd another, or type `done`."));
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (!_sessions.TryGetValue(component.User.Id, out var s))
        {
            await component.RespondAsync("That event setup has expired. Run `/event` to start again.");
            return;
        }
        if (IsExpired(s))
        {
            _sessions.TryRemove(component.User.Id, out _);
            await ClearButtons(component, "⌛ Timed out. Run `/event` again.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;

        var parts = component.Data.CustomId.Split(':'); // evtwiz:<kind>:<arg?>
        var kind  = parts.Length > 1 ? parts[1] : string.Empty;
        var arg   = parts.Length > 2 ? parts[2] : string.Empty;

        try
        {
            switch (kind)
            {
                case "tz":      await OnTimezoneButtonAsync(s, component, arg);   break;
                case "rec":     await OnRecurrenceButtonAsync(s, component, arg); break;
                case "confirm": await OnConfirmAsync(s, component);               break;
                case "cancel":  await OnCancelAsync(s, component);                break;
                default:        await component.DeferAsync();                     break;
            }
        }
        catch (Exception ex)
        {
            // Don't swallow silently: a throw here (e.g. building the confirm card)
            // otherwise looks like the button "did nothing". Surface it so the
            // organizer knows to retry rather than staring at a dead button.
            _logger.LogError(ex, "Wizard button {CustomId} failed", component.Data.CustomId);
            await SafeSend(s, "⚠️ Something went wrong on that step. Type `cancel` and run `/event` to start over.");
        }
    }

    private async Task OnTimezoneButtonAsync(EventCreationSession s, SocketMessageComponent c, string arg)
    {
        if (!int.TryParse(arg, out var idx) || idx < 0 || idx >= EventTimeParser.CommonZones.Count)
        {
            await c.DeferAsync();
            return;
        }

        var (label, iana) = EventTimeParser.CommonZones[idx];
        await SaveTimeZoneAsync(s.Draft.OrganizerId, iana);
        s.Draft.TimeZoneId = iana;
        s.Step = WizardStep.Title;

        await ClearButtons(c, $"Timezone set to **{label}** ({iana}).");
        await s.Dm.SendMessageAsync("🎯 What's the **title** of the event?");
    }

    private async Task OnRecurrenceButtonAsync(EventCreationSession s, SocketMessageComponent c, string arg)
    {
        if (arg == "none")
        {
            s.Draft.Frequency = null;
            await ClearButtons(c, "Repeats: **does not repeat**.");
            await PromptConfirmAsync(s);
            return;
        }

        if (arg == "dates")
        {
            s.Draft.Frequency = ClanEventFrequency.Custom;
            s.Step = WizardStep.SpecificDates;
            await ClearButtons(c, "Repeats: **specific dates**.");
            await s.Dm.SendMessageAsync(embed: Form("🗓️ Specific dates",
                $"First date is **{EventTimeParser.Stamp(s.Draft.StartUtc, 'F')}**.\n\n" +
                "Send another date & time (e.g. `Thursday 8pm`), one at a time. Type `done` when you've added them all."));
            return;
        }

        ClanEventFrequency? freq = arg switch
        {
            "daily"    => ClanEventFrequency.Daily,
            "weekly"   => ClanEventFrequency.Weekly,
            "biweekly" => ClanEventFrequency.Biweekly,
            "monthly"  => ClanEventFrequency.Monthly,
            _          => null,
        };

        if (freq is null)
        {
            await c.DeferAsync();
            return;
        }

        s.Draft.Frequency = freq;
        s.Step = WizardStep.RecurrenceUntil;
        await ClearButtons(c, $"Repeats: **{freq}**.");
        await s.Dm.SendMessageAsync(embed: Form("🔁 Until when?", "Give an **end date** (`August 1`), a **number of times** (`8`), or `none` for open-ended."));
    }

    private async Task OnConfirmAsync(EventCreationSession s, SocketMessageComponent c)
    {
        // Duplicate-event guard. If this draft overlaps an existing scheduled
        // event and the organizer hasn't already been warned, surface the
        // conflict and require a second, explicit confirm. This is the backstop
        // for the common failure where someone believes they cancelled an old
        // recurring event (but the cancel never actually completed) and creates a
        // parallel series — which is exactly how duplicate calendar entries arise.
        if (!s.OverlapAcknowledged)
        {
            var conflicts = await FindOverlappingScheduledAsync(s.Draft);
            if (conflicts.Count > 0)
            {
                s.OverlapAcknowledged = true;          // a second confirm click now proceeds
                s.LastActivityAt = DateTime.UtcNow;
                await ShowOverlapWarningAsync(s, c, conflicts);
                return;
            }
        }

        _sessions.TryRemove(s.Draft.OrganizerId, out _);

        // Acknowledge immediately and strip the buttons (also prevents a
        // double-submit). Publishing a recurring series posts several messages,
        // which can exceed Discord's 3-second interaction window — so we ack
        // here, publish, then edit the message to its final state.
        try
        {
            await c.UpdateAsync(m =>
            {
                m.Content    = "⏳ Creating your event…";
                m.Embed      = null;
                m.Components  = new ComponentBuilder().Build();
                m.Attachments = new List<FileAttachment>(); // drop the preview image
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Confirm ack (UpdateAsync) failed for {User}", s.Draft.OrganizerId);
        }

        try
        {
            if (s.Draft.Frequency is null)
                await _publisher.PublishOneOffAsync(s.Draft);
            else if (s.Draft.Frequency == ClanEventFrequency.Custom)
                await _publisher.PublishSpecificDatesAsync(s.Draft);
            else
                await _publisher.PublishSeriesAsync(s.Draft);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing event '{Title}' failed for {User}", s.Draft.Title, s.Draft.OrganizerId);
            await FinalizeConfirmAsync(c, "❌ Something went wrong creating the event. Please try `/event` again.");
            return;
        }

        var postChannelId = _config.GetEventPostChannelId();
        var channelMention = postChannelId != 0 ? $"<#{postChannelId}>" : "the events channel";
        await FinalizeConfirmAsync(c, $"✅ **Event created!** It's been posted to {channelMention}.");
    }

    /// <summary>
    /// Returns up to a handful of scheduled events whose time window overlaps the
    /// draft's first occurrence. Used by the confirm step's duplicate guard. We
    /// check the first occurrence only — for a recurring duplicate (e.g. a weekly
    /// event recreated at the same weekday/time) the first occurrences collide,
    /// which is enough to flag it.
    /// </summary>
    private async Task<List<ClanEvent>> FindOverlappingScheduledAsync(EventDraft d)
    {
        var start = d.StartUtc;
        var end   = d.EndUtc;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.ClanEvents
            .Where(e => e.GuildId == d.GuildId
                     && e.Status == ClanEventStatus.Scheduled
                     && e.StartUtc < end
                     && e.EndUtc   > start)
            .OrderBy(e => e.StartUtc)
            .Take(5)
            .ToListAsync();
    }

    /// <summary>
    /// Replaces the confirm card with an overlap warning. The "Create anyway"
    /// button reuses the existing confirm action (the session's
    /// OverlapAcknowledged flag is now set, so it won't loop), and "Cancel"
    /// reuses the normal cancel action.
    /// </summary>
    private async Task ShowOverlapWarningAsync(
        EventCreationSession s, SocketMessageComponent c, List<ClanEvent> conflicts)
    {
        var lines = string.Join("\n", conflicts.Select(e =>
            $"• **{e.Title}** — {EventTimeParser.Stamp(e.StartUtc, 'F')}"));

        var embed = new EmbedBuilder()
            .WithTitle("⚠️ This overlaps an existing event")
            .WithColor(Color.Orange)
            .WithDescription(
                $"**{s.Draft.Title}** ({EventTimeParser.Stamp(s.Draft.StartUtc, 'F')}) overlaps:\n\n" +
                lines +
                "\n\nIf you meant to **change** an existing event, cancel this and use `/event edit` " +
                "on the existing one instead — creating a new event leaves a duplicate on the calendar. " +
                "If this is genuinely a separate event, go ahead and create it.")
            .Build();

        var buttons = new ComponentBuilder()
            .WithButton("Create anyway", $"{Prefix}confirm", ButtonStyle.Danger)
            .WithButton("Cancel",        $"{Prefix}cancel",  ButtonStyle.Secondary)
            .Build();

        try
        {
            await c.UpdateAsync(m =>
            {
                m.Content     = string.Empty;
                m.Embed       = embed;
                m.Components   = buttons;
                m.Attachments = new List<FileAttachment>(); // drop the preview image
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to show overlap warning for {User}", s.Draft.OrganizerId);
        }
    }

    /// <summary>
    /// Edits the already-acknowledged confirm message to its final text. Used
    /// instead of <see cref="ClearButtons"/> on the confirm path because that
    /// path has already called UpdateAsync (the immediate ack), and a component
    /// interaction can only be updated once.
    /// </summary>
    private async Task FinalizeConfirmAsync(SocketMessageComponent c, string content)
    {
        try
        {
            await c.ModifyOriginalResponseAsync(m =>
            {
                m.Content    = content;
                m.Embed      = null;
                m.Components  = new ComponentBuilder().Build();
                m.Attachments = new List<FileAttachment>(); // no lingering preview image
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to finalize confirm message");
        }
    }

    private async Task OnCancelAsync(EventCreationSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.OrganizerId, out _);
        await ClearButtons(c, "❌ Cancelled. Nothing was created.");
    }

    // ─── Prompts ───────────────────────────────────────────────────────────

    private async Task PromptTimezoneAsync(EventCreationSession s)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < EventTimeParser.CommonZones.Count; i++)
        {
            var (label, _) = EventTimeParser.CommonZones[i];
            builder.WithButton(label, $"{Prefix}tz:{i}", ButtonStyle.Secondary, row: i / 5);
        }

        await s.Dm.SendMessageAsync(
            "🎯 **New event!** First — what's your **timezone**? Tap one below, or just type it (e.g. `America/Chicago`).",
            components: builder.Build());
    }

    private async Task PromptRecurrenceAsync(EventCreationSession s)
    {
        var builder = new ComponentBuilder()
            .WithButton("Does not repeat", $"{Prefix}rec:none",     ButtonStyle.Secondary)
            .WithButton("Daily",           $"{Prefix}rec:daily",    ButtonStyle.Secondary)
            .WithButton("Weekly",          $"{Prefix}rec:weekly",   ButtonStyle.Secondary)
            .WithButton("Biweekly",        $"{Prefix}rec:biweekly", ButtonStyle.Secondary)
            .WithButton("Monthly",         $"{Prefix}rec:monthly",  ButtonStyle.Secondary)
            .WithButton("Specific dates",  $"{Prefix}rec:dates",    ButtonStyle.Secondary);

        await s.Dm.SendMessageAsync(embed: Form("🔁 Does this event repeat?",
            "Pick an option. **Specific dates** is for several set nights that don't follow a pattern."), components: builder.Build());
    }

    private async Task PromptConfirmAsync(EventCreationSession s)
    {
        s.Step = WizardStep.Confirm;
        var d = s.Draft;

        var embed = new EmbedBuilder()
            .WithTitle("📋 Confirm event")
            .WithColor(Color.Blue)
            .AddField("Title", d.Title)
            .AddField("When", $"{EventTimeParser.Stamp(d.StartUtc, 'F')} ({EventTimeParser.Stamp(d.StartUtc, 'R')})")
            .AddField("Ends", EventTimeParser.Stamp(d.EndUtc, 't'), inline: true)
            .AddField("Duration", FormatDuration(d.StartUtc, d.EndUtc), inline: true)
            .AddField("Repeats", DescribeRecurrence(d), inline: true);

        embed.AddField("Attendee limit", d.MaxParticipants is int cap ? $"{cap} max (waitlist past that)" : "No limit", inline: true);

        // For specific-dates, list every night so the creator can sanity-check.
        if (d.Frequency == ClanEventFrequency.Custom)
        {
            var all = new List<DateTime> { d.StartUtc };
            all.AddRange(d.SpecificDatesUtc);
            all.Sort();
            embed.AddField("Dates", string.Join("\n", all.Select(u => $"• {EventTimeParser.Stamp(u, 'f')}")));
        }

        // Truncate for the PREVIEW only: this card renders the description as an
        // embed FIELD (Discord caps field values at 1024 chars), whereas the live
        // #events post uses WithDescription (4096 limit) and keeps the full text.
        // Without this, a 1025–4096-char description threw inside AddField — which
        // was swallowed on the button path ("Does not repeat" did nothing) and
        // surfaced as a generic "Something went wrong" on the typed-recurrence path.
        if (!string.IsNullOrWhiteSpace(d.Description))
            embed.AddField("Description", Truncate(d.Description, 1024));

        // Make the assumed zone visible: the times above render in each viewer's
        // own local clock, but the creator's text input was interpreted in this
        // zone. If it's wrong, /timezone sets a personal override.
        var zoneLabel = string.IsNullOrWhiteSpace(d.TimeZoneId) ? _config.EventDefaultTimeZone : d.TimeZoneId;
        embed.WithFooter($"Read in {zoneLabel} • shown in your local time • /timezone to change");

        // Compact thumbnail in the DM preview to keep it tidy — the actual
        // #events post still shows the full-width banner (via BuildEmbed).
        if (!string.IsNullOrWhiteSpace(d.ImageFileName))
            embed.WithThumbnailUrl($"attachment://{d.ImageFileName}");

        var buttons = new ComponentBuilder()
            .WithButton("Create event", $"{Prefix}confirm", ButtonStyle.Success)
            .WithButton("Cancel",       $"{Prefix}cancel",  ButtonStyle.Danger);

        if (d.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(d.ImageFileName))
        {
            using var fa = new FileAttachment(new MemoryStream(d.ImageBytes), d.ImageFileName);
            await s.Dm.SendFileAsync(fa,
                text: "Here's how it'll look — create it?", embed: embed.Build(), components: buttons.Build());
        }
        else
        {
            await s.Dm.SendMessageAsync(
                "Here's how it'll look — create it?", embed: embed.Build(), components: buttons.Build());
        }
    }

    // ─── Helpers ───────────────────────────────────────────────────────────

    private async Task SaveTimeZoneAsync(ulong userId, string iana)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var row = await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == userId);
        if (row is null)
            db.UserTimeZones.Add(new UserTimeZone { UserId = userId, IanaId = iana, UpdatedAt = DateTime.UtcNow });
        else
        {
            row.IanaId    = iana;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static string DescribeRecurrence(EventDraft d)
    {
        if (d.Frequency is null) return "Does not repeat";
        if (d.Frequency == ClanEventFrequency.Custom)
            return $"Specific dates ({1 + d.SpecificDatesUtc.Count})";
        var bound = d.MaxOccurrences is int n ? $", {n} times"
                  : d.UntilUtc is DateTime u ? $", until {EventTimeParser.Stamp(u, 'd')}"
                  : ", ongoing";
        return $"{d.Frequency}{bound}";
    }

    /// <summary>Caps a value to Discord's 1024-char embed-field limit, with an ellipsis.</summary>
    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string FormatDuration(DateTime startUtc, DateTime endUtc)
    {
        var span = endUtc - startUtc;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min";
        var hours = (int)span.TotalHours;
        var mins  = span.Minutes;
        return mins == 0 ? $"{hours}h" : $"{hours}h {mins}m";
    }

    private static readonly Color FormColor = new(0x5865F2);

    /// <summary>Builds the consistent "form" embed used for every wizard prompt.</summary>
    private static Embed Form(string title, string? body = null)
    {
        var eb = new EmbedBuilder()
            .WithColor(FormColor)
            .WithTitle(title)
            .WithFooter("Reply in this DM • type \"cancel\" to quit • times out after 15 min");
        if (!string.IsNullOrWhiteSpace(body)) eb.WithDescription(body);
        return eb.Build();
    }

    /// <summary>Times out idle sessions: DMs the organizer, then drops the session.</summary>
    private async Task SweepIdleAsync()
    {
        try
        {
            foreach (var kv in _sessions)
            {
                if (!IsExpired(kv.Value)) continue;
                if (_sessions.TryRemove(kv.Key, out var s))
                {
                    try
                    {
                        await s.Dm.SendMessageAsync(embed: Form(
                            "⌛ Event setup timed out",
                            "Looks like you stepped away — I didn't hear back, so I've cancelled this setup and **nothing was created**. Run `/event` whenever you're ready to start again."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send wizard timeout DM to {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Wizard idle sweep failed"); }
    }

    private static bool IsExpired(EventCreationSession s) =>
        DateTime.UtcNow - s.LastActivityAt > IdleTimeout;

    private void PruneExpired()
    {
        foreach (var kv in _sessions)
            if (IsExpired(kv.Value))
                _sessions.TryRemove(kv.Key, out _);
    }

    private async Task ClearButtons(SocketMessageComponent c, string content)
    {
        await c.UpdateAsync(m =>
        {
            m.Content    = content;
            m.Embed      = null;
            m.Components = new ComponentBuilder().Build();
        });
    }

    private async Task SafeSend(EventCreationSession s, string content)
    {
        try { await s.Dm.SendMessageAsync(content); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send wizard DM to {User}", s.Draft.OrganizerId); }
    }

    private async Task SafeSendEmbed(EventCreationSession s, Embed embed)
    {
        try { await s.Dm.SendMessageAsync(embed: embed); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send wizard DM to {User}", s.Draft.OrganizerId); }
    }
}
