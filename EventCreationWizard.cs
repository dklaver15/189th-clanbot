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
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private const string Prefix = "evtwiz:";

    private readonly ConcurrentDictionary<ulong, EventCreationSession> _sessions = new();

    private readonly IServiceProvider _services;
    private readonly ILogger<EventCreationWizard> _logger;
    private readonly BotConfig _config;
    private readonly EventTimeParser _time;
    private readonly IEventPublisher _publisher;

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
                OrganizerName = user.GlobalName ?? user.Username,
                TimeZoneId    = tz ?? string.Empty,
            },
        };
        _sessions[user.Id] = session;

        try
        {
            if (session.TimezoneKnown)
            {
                session.Step = WizardStep.Title;
                await dm.SendMessageAsync("🎯 **New event!** What's the **title**?");
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
            await SafeSend(s, "⌛ That event setup timed out. Run `/event` again to start over.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        try
        {
            switch (s.Step)
            {
                case WizardStep.Timezone:        await HandleTimezoneTextAsync(s, text);   break;
                case WizardStep.Title:           await HandleTitleAsync(s, text);          break;
                case WizardStep.When:            await HandleWhenAsync(s, text);           break;
                case WizardStep.Duration:        await HandleDurationAsync(s, text);       break;
                case WizardStep.Description:     await HandleDescriptionAsync(s, text);    break;
                case WizardStep.RecurrenceUntil: await HandleRecurrenceUntilAsync(s, text); break;
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
            await s.Dm.SendMessageAsync("Give the event a title — e.g. `Friday Night Ops`.");
            return;
        }
        if (text.Length > 100)
        {
            await s.Dm.SendMessageAsync("That title is a bit long (100 characters max). Try a shorter one.");
            return;
        }

        s.Draft.Title = text;
        s.Step = WizardStep.When;
        await s.Dm.SendMessageAsync(
            "🕒 **When** is it? You can say things like `7pm tomorrow`, `in 2 hours`, or `June 14 at 8pm`.");
    }

    private async Task HandleWhenAsync(EventCreationSession s, string text)
    {
        var tz = _time.ResolveZone(s.Draft.TimeZoneId);
        var r  = _time.ParseStart(text, tz);

        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(r.Error);
            return;
        }
        if (!r.HasTimeOfDay)
        {
            await s.Dm.SendMessageAsync(
                "I got a date but no time of day — include a time too, like `June 14 at 7pm`.");
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
            await s.Dm.SendMessageAsync($"{echo}\n⏱️ Ends: {EventTimeParser.Stamp(end, 't')}");
            await AdvanceToDescriptionAsync(s);
        }
        else
        {
            s.Step = WizardStep.Duration;
            await s.Dm.SendMessageAsync($"{echo}\n\n⏱️ How long does it run? e.g. `2 hours`, `90 minutes`, or `until 9pm`.");
        }
    }

    private async Task HandleDurationAsync(EventCreationSession s, string text)
    {
        var tz = _time.ResolveZone(s.Draft.TimeZoneId);
        var r  = _time.ParseEnd(text, s.Draft.StartUtc, tz);

        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(r.Error);
            return;
        }

        s.Draft.EndUtc = r.EndUtc;
        await AdvanceToDescriptionAsync(s);
    }

    private async Task AdvanceToDescriptionAsync(EventCreationSession s)
    {
        s.Step = WizardStep.Description;
        await s.Dm.SendMessageAsync("📝 Add a **description** (or type `skip`).");
    }

    private async Task HandleDescriptionAsync(EventCreationSession s, string text)
    {
        s.Draft.Description = text.Equals("skip", StringComparison.OrdinalIgnoreCase)
                           || text.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : text;

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
                await s.Dm.SendMessageAsync("Tell me an end date (`August 1`), a number of times (`8`), or `none` for open-ended.");
                return;
            }
            s.Draft.UntilUtc = r.StartUtc;
            s.Draft.MaxOccurrences = null;
        }

        await PromptConfirmAsync(s);
    }

    // ─── Buttons (timezone / recurrence / confirm / cancel) ────────────────

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
            _logger.LogError(ex, "Wizard button {CustomId} failed", component.Data.CustomId);
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
        await s.Dm.SendMessageAsync(
            "Until when should it repeat? Give an **end date** (`August 1`), a **number of times** (`8`), or `none` for open-ended.");
    }

    private async Task OnConfirmAsync(EventCreationSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.OrganizerId, out _);

        try
        {
            if (s.Draft.Frequency is null)
                await _publisher.PublishOneOffAsync(s.Draft);
            else
                await _publisher.PublishSeriesAsync(s.Draft);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing event '{Title}' failed for {User}", s.Draft.Title, s.Draft.OrganizerId);
            await ClearButtons(c, "❌ Something went wrong creating the event. Please try `/event` again.");
            return;
        }

        var postChannelId = _config.GetEventPostChannelId();
        var channelMention = postChannelId != 0 ? $"<#{postChannelId}>" : "the events channel";
        await ClearButtons(c, $"✅ **Event created!** It's been posted to {channelMention}.");
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
            .WithButton("Monthly",         $"{Prefix}rec:monthly",  ButtonStyle.Secondary);

        await s.Dm.SendMessageAsync("🔁 Does this event **repeat**?", components: builder.Build());
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

        if (!string.IsNullOrWhiteSpace(d.Description))
            embed.AddField("Description", d.Description);

        var buttons = new ComponentBuilder()
            .WithButton("Create event", $"{Prefix}confirm", ButtonStyle.Success)
            .WithButton("Cancel",       $"{Prefix}cancel",  ButtonStyle.Danger);

        await s.Dm.SendMessageAsync(
            "Here's how it'll look — create it?", embed: embed.Build(), components: buttons.Build());
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
        var bound = d.MaxOccurrences is int n ? $", {n} times"
                  : d.UntilUtc is DateTime u ? $", until {EventTimeParser.Stamp(u, 'd')}"
                  : ", ongoing";
        return $"{d.Frequency}{bound}";
    }

    private static string FormatDuration(DateTime startUtc, DateTime endUtc)
    {
        var span = endUtc - startUtc;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min";
        var hours = (int)span.TotalHours;
        var mins  = span.Minutes;
        return mins == 0 ? $"{hours}h" : $"{hours}h {mins}m";
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
}
