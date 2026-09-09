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
/// The DM reminder-creation wizard — a leaner sibling of
/// <see cref="EventCreationWizard"/>. Triggered by /reminder create (or /reminder
/// edit), it walks the creator through title → when → description → image → link →
/// channel → ping → recurrence → confirm in their DMs, then writes a
/// <see cref="ClanReminder"/> row. The actual posting happens later, at fire time,
/// in <see cref="Services.ReminderSchedulerService"/>.
///
/// ── Why no RSVP / publisher ──
/// A reminder is just a scheduled announcement: no attendees, no calendar hub, no
/// Google Calendar projection. So unlike the event wizard it persists a single
/// self-rescheduling row and returns — there is nothing to post until the
/// scheduler fires it.
///
/// ── Routing / sessions / times ──
/// Same shape as the event wizard: self-registers MessageReceived (DM text) and
/// ButtonExecuted (buttons prefixed "remwiz:"), sessions are in-memory keyed by
/// user id and time out after <see cref="IdleTimeout"/>, and all time parsing
/// routes through <see cref="EventTimeParser"/> in the creator's zone (the draft
/// carries UTC; display uses Discord &lt;t:unix&gt; markdown).
/// </summary>
public sealed class ReminderCreationWizard
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private const string Prefix = "remwiz:";

    private readonly ConcurrentDictionary<ulong, ReminderCreationSession> _sessions = new();

    // Shared client for pulling an uploaded image off the Discord CDN.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly IServiceProvider _services;
    private readonly ILogger<ReminderCreationWizard> _logger;
    private readonly BotConfig _config;
    private readonly EventTimeParser _time;
    private readonly DiscordSocketClient _client;
    private readonly System.Threading.Timer _idleSweep;

    public ReminderCreationWizard(
        IServiceProvider services,
        ILogger<ReminderCreationWizard> logger,
        IOptions<BotConfig> config,
        EventTimeParser time,
        DiscordSocketClient client)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
        _time     = time;
        _client   = client;

        _idleSweep = new System.Threading.Timer(_ => _ = SweepIdleAsync(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived += OnMessageReceivedAsync;
        client.ButtonExecuted  += OnButtonExecutedAsync;
    }

    // ─── Entry points (called by ReminderCommandHandler) ───────────────────

    /// <summary>
    /// Opens a DM and starts a fresh create wizard. The channel (and any starter
    /// ping) come from the native pickers on /reminder create — DMs can't render
    /// guild pickers — and are seeded here, so the DM skips the channel step.
    /// False if DMs are closed.
    /// </summary>
    public Task<bool> StartCreateAsync(
        IUser user, ulong guildId, ulong channelId, string channelName,
        List<ulong>? seedRoleIds = null, List<ulong>? seedUserIds = null,
        bool seedEveryone = false, bool seedHere = false) =>
        StartAsync(user, guildId, existing: null, channelId, channelName, seedRoleIds, seedUserIds, seedEveryone, seedHere);

    /// <summary>Opens a DM and starts the wizard pre-seeded to EDIT an existing reminder.</summary>
    public Task<bool> StartEditAsync(IUser user, ClanReminder existing) => StartAsync(user, existing.GuildId, existing);

    private async Task<bool> StartAsync(
        IUser user, ulong guildId, ClanReminder? existing,
        ulong channelId = 0, string channelName = "", List<ulong>? seedRoleIds = null, List<ulong>? seedUserIds = null,
        bool seedEveryone = false, bool seedHere = false)
    {
        PruneExpired();

        var (dm, dmFailure) = await DmGuard.TryOpenAsync(user);
        if (dm is null)
        {
            _logger.LogInformation("Could not open DM with {User} for /reminder: {Failure}", user.Id, dmFailure);
            return false;
        }

        string? tz;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            tz = (await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == user.Id))?.IanaId;
        }
        // When editing, the reminder already carries a zone — prefer it if the
        // creator has no personal one saved.
        if (string.IsNullOrWhiteSpace(tz) && existing is not null && !string.IsNullOrWhiteSpace(existing.TimeZoneId))
            tz = existing.TimeZoneId;

        var draft = new ReminderDraft
        {
            GuildId     = guildId,
            CreatorId   = user.Id,
            CreatorName = (user as SocketGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username,
            TimeZoneId  = tz ?? string.Empty,
        };

        if (existing is not null)
        {
            // Seed every field so each step can offer "keep" to retain it.
            draft.EditingReminderId = existing.Id;
            draft.Title         = existing.Title;
            draft.Description   = existing.Description;
            draft.Url           = existing.Url;
            draft.FirstFireUtc  = existing.NextFireUtc;
            draft.ChannelId     = existing.ChannelId;
            draft.ChannelName   = _client.GetGuild(guildId)?.GetTextChannel(existing.ChannelId)?.Name ?? "the chosen channel";
            draft.PingRoleIds   = SplitIds(existing.PingRoleIdsCsv);
            draft.PingUserIds   = SplitIds(existing.PingUserIdsCsv);
            draft.PingEveryone  = existing.PingEveryone;
            draft.PingHere      = existing.PingHere;
            draft.ImageBytes    = existing.ImageBytes;
            draft.ImageFileName = existing.ImageFileName;
        }
        else
        {
            // Create: channel came from the native picker; an optional starter
            // ping (a single role or member) may have too.
            draft.ChannelId    = channelId;
            draft.ChannelName  = channelName;
            draft.PingRoleIds  = seedRoleIds ?? new();
            draft.PingUserIds  = seedUserIds ?? new();
            draft.PingEveryone = seedEveryone;
            draft.PingHere     = seedHere;
        }

        var session = new ReminderCreationSession
        {
            Dm             = dm,
            StartedAt      = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow,
            TimezoneKnown  = !string.IsNullOrWhiteSpace(tz),
            Draft          = draft,
        };
        _sessions[user.Id] = session;

        try
        {
            var opener = existing is null ? "🔔 New reminder" : "✏️ Editing reminder";
            if (session.TimezoneKnown)
            {
                session.Step = ReminderWizardStep.Title;
                await dm.SendMessageAsync(embed: Form(opener,
                    existing is null
                        ? "What's the **title** of the reminder? (Shown as the embed heading.)"
                        : $"What's the **title**? Current: **{existing!.Title}** — type a new one, or `keep`."));
            }
            else
            {
                session.Step = ReminderWizardStep.Timezone;
                await PromptTimezoneAsync(session);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening DM to {User} for /reminder", user.Id);
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
            await SafeSendEmbed(s, Form("⌛ Reminder setup timed out",
                "That setup expired from inactivity and **nothing was saved**. Run `/reminder create` to start over."));
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSendEmbed(s, Form("Cancelled", "No reminder was saved. Run `/reminder create` to start again anytime."));
            return;
        }

        try
        {
            switch (s.Step)
            {
                case ReminderWizardStep.Timezone:        await HandleTimezoneTextAsync(s, text);      break;
                case ReminderWizardStep.Title:           await HandleTitleAsync(s, text);             break;
                case ReminderWizardStep.When:            await HandleWhenAsync(s, text);              break;
                case ReminderWizardStep.Description:     await HandleDescriptionAsync(s, text);       break;
                case ReminderWizardStep.Image:           await HandleImageAsync(s, message, text);    break;
                case ReminderWizardStep.Link:            await HandleLinkAsync(s, text);              break;
                case ReminderWizardStep.Channel:         await HandleChannelAsync(s, text);           break;
                case ReminderWizardStep.RecurrenceUntil: await HandleRecurrenceUntilAsync(s, text);   break;
                // Recurrence / Confirm are button steps — ignore stray text.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reminder wizard text step {Step} failed for {User}", s.Step, message.Author.Id);
            _sessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, "Something went wrong. Run `/reminder create` to start over.");
        }
    }

    private async Task HandleTimezoneTextAsync(ReminderCreationSession s, string text)
    {
        if (!_time.TryResolveTimeZone(text, out _, out var iana))
        {
            await s.Dm.SendMessageAsync(
                "I didn't recognize that timezone. Try `Central` / `CST`, or an IANA id like `America/Chicago` — or tap a button above.");
            return;
        }

        await SaveTimeZoneAsync(s.Draft.CreatorId, iana);
        s.Draft.TimeZoneId = iana;
        s.Step = ReminderWizardStep.Title;
        await s.Dm.SendMessageAsync($"Timezone set to **{iana}**.\n\n🔔 What's the **title** of the reminder?");
    }

    private async Task HandleTitleAsync(ReminderCreationSession s, string text)
    {
        if (IsKeep(s, text) && !string.IsNullOrWhiteSpace(s.Draft.Title))
        {
            await AdvanceToWhenAsync(s);
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Title", "Give the reminder a title — e.g. `HLL: Vietnam playtest opens soon`."));
            return;
        }
        if (text.Length > 256)
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Title too long", "Keep it under 256 characters — try a shorter one."));
            return;
        }

        s.Draft.Title = text;
        await AdvanceToWhenAsync(s);
    }

    private async Task AdvanceToWhenAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.When;
        var extra = s.Draft.EditingReminderId is not null && s.Draft.FirstFireUtc != default
            ? $"\n\nCurrent: **{EventTimeParser.Stamp(s.Draft.FirstFireUtc, 'F')}** — type a new time, or `keep`."
            : string.Empty;
        await s.Dm.SendMessageAsync(embed: Form("🕒 When should it post?",
            "This is the exact moment the announcement goes out. Say things like `Friday 7:45am`, " +
            "`in 2 hours`, or `July 24 at 8am`." + extra));
    }

    private async Task HandleWhenAsync(ReminderCreationSession s, string text)
    {
        if (IsKeep(s, text) && s.Draft.FirstFireUtc != default)
        {
            await AdvanceToDescriptionAsync(s);
            return;
        }

        var tz = _time.ResolveZone(s.Draft.TimeZoneId);
        var r  = _time.ParseStart(text, tz);

        if (!r.Success)
        {
            await s.Dm.SendMessageAsync(embed: Form("Let's try that again", r.Error));
            return;
        }
        if (!r.HasTimeOfDay)
        {
            await s.Dm.SendMessageAsync(embed: Form("🕒 Need a time of day", "I got a date but no time — include a time too, like `July 24 at 8am`."));
            return;
        }
        if (r.StartUtc <= DateTime.UtcNow)
        {
            await s.Dm.SendMessageAsync(embed: Form("🕒 That's in the past", "Pick a future date & time for the reminder to post."));
            return;
        }

        s.Draft.FirstFireUtc = r.StartUtc;

        var echo = $"📅 Posts: {EventTimeParser.Stamp(r.StartUtc, 'F')} ({EventTimeParser.Stamp(r.StartUtc, 'R')})";
        if (r.WasAmbiguous)
            echo += "\n*(I picked the soonest matching time — you can double-check at the end.)*";
        await s.Dm.SendMessageAsync(embed: Form("🕒 Time set", echo));
        await AdvanceToDescriptionAsync(s);
    }

    private async Task AdvanceToDescriptionAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.Description;
        var cur = s.Draft.EditingReminderId is not null
            ? (string.IsNullOrWhiteSpace(s.Draft.Description) ? " Current: *(none)* — type `keep` to leave it empty." : " Type `keep` to leave it as-is.")
            : string.Empty;
        await s.Dm.SendMessageAsync(embed: Form("📝 Description",
            "Add the body text for the announcement, or type `skip`." + cur));
    }

    private async Task HandleDescriptionAsync(ReminderCreationSession s, string text)
    {
        if (IsKeep(s, text)) { await PromptImageAsync(s); return; }

        if (text.Length > 4096 && !IsSkip(text))
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Description too long",
                "Keep it under 4096 characters — trim it down, or type `skip`."));
            return;
        }

        s.Draft.Description = IsSkip(text) ? string.Empty : text;
        await PromptImageAsync(s);
    }

    private async Task PromptImageAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.Image;
        var cur = s.Draft.EditingReminderId is not null && !string.IsNullOrWhiteSpace(s.Draft.ImageFileName)
            ? " Type `keep` to keep the current image."
            : string.Empty;
        await s.Dm.SendMessageAsync(embed: Form("🖼️ Image",
            "Drag an image into this DM, or paste a GIF/image link (Tenor, Giphy, or direct — PNG/JPG/GIF/WebP, max 8 MB). Or type `skip`." + cur));
    }

    private async Task HandleImageAsync(ReminderCreationSession s, SocketMessage message, string text)
    {
        if (IsKeep(s, text) && !string.IsNullOrWhiteSpace(s.Draft.ImageFileName))
        {
            await PromptLinkAsync(s);
            return;
        }

        if (IsSkip(text))
        {
            s.Draft.ImageBytes    = null;
            s.Draft.ImageFileName = null;
            await PromptLinkAsync(s);
            return;
        }

        var att = message.Attachments.FirstOrDefault();
        if (att is null)
        {
            // No file dragged in — maybe a GIF/image link was pasted.
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
                await PromptLinkAsync(s);
                return;
            }

            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image", "Drag an image into this DM, paste a GIF/image link, or type `skip`."));
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
        try { bytes = await Http.GetByteArrayAsync(att.Url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download reminder image for {User}", s.Draft.CreatorId);
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
        await PromptLinkAsync(s);
    }

    private async Task PromptLinkAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.Link;
        var cur = s.Draft.EditingReminderId is not null
            ? (string.IsNullOrWhiteSpace(s.Draft.Url) ? " Current: *(none)*." : $" Current: {s.Draft.Url} — type `keep` to keep it.")
            : string.Empty;
        await s.Dm.SendMessageAsync(embed: Form("🔗 Link",
            "Add a link to include in the announcement (e.g. a blog post with instructions), or type `skip`." + cur));
    }

    private async Task HandleLinkAsync(ReminderCreationSession s, string text)
    {
        if (IsKeep(s, text)) { await AdvanceAfterLinkAsync(s); return; }

        if (IsSkip(text))
        {
            s.Draft.Url = null;
            await AdvanceAfterLinkAsync(s);
            return;
        }

        if (!(text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(text, UriKind.Absolute, out _))
        {
            await s.Dm.SendMessageAsync(embed: Form("🔗 That doesn't look like a link",
                "Paste a full URL starting with `https://`, or type `skip`."));
            return;
        }
        if (text.Length > 400)
        {
            await s.Dm.SendMessageAsync(embed: Form("🔗 Link too long", "That URL is unusually long — double-check it, or type `skip`."));
            return;
        }

        s.Draft.Url = text;
        await AdvanceAfterLinkAsync(s);
    }

    /// <summary>
    /// After the link step: on a fresh create the channel is already set (from the
    /// command's native picker), so jump to the ping step; when editing, offer the
    /// channel step so it can be changed.
    /// </summary>
    private async Task AdvanceAfterLinkAsync(ReminderCreationSession s)
    {
        if (s.Draft.EditingReminderId is null)
            await PromptRecurrenceAsync(s);
        else
            await PromptChannelAsync(s);
    }

    private async Task PromptChannelAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.Channel;
        var cur = s.Draft.EditingReminderId is not null && s.Draft.ChannelId != 0
            ? $" Current: **#{s.Draft.ChannelName}** — type `keep` to leave it."
            : string.Empty;
        await s.Dm.SendMessageAsync(embed: Form("📢 Which channel?",
            "Where should this post? Paste the **channel link** (right-click → Copy Link), its **id**, or type its **name**." + cur));
    }

    private async Task HandleChannelAsync(ReminderCreationSession s, string text)
    {
        if (IsKeep(s, text) && s.Draft.ChannelId != 0)
        {
            await PromptRecurrenceAsync(s);
            return;
        }

        var guild = _client.GetGuild(s.Draft.GuildId);
        if (guild is null)
        {
            await s.Dm.SendMessageAsync(embed: Form("Hmm", "I can't reach that server right now. Try again in a moment, or type `cancel`."));
            return;
        }

        if (!ReminderTargets.TryResolveChannel(guild, text, out var channel, out var error) || channel is null)
        {
            await s.Dm.SendMessageAsync(embed: Form("📢 Couldn't find that channel", error));
            return;
        }

        // Make sure the bot can actually post there before locking it in.
        var me = guild.CurrentUser;
        var perms = me.GetPermissions(channel);
        if (!perms.ViewChannel || !perms.SendMessages)
        {
            await s.Dm.SendMessageAsync(embed: Form("📢 I can't post there",
                $"I don't have permission to send messages in **#{channel.Name}**. Pick another channel, or fix my permissions and try again."));
            return;
        }

        s.Draft.ChannelId   = channel.Id;
        s.Draft.ChannelName = channel.Name;
        await PromptRecurrenceAsync(s);
    }

    private async Task HandleRecurrenceUntilAsync(ReminderCreationSession s, string text)
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
                await s.Dm.SendMessageAsync(embed: Form("🔁 Until when?", "Tell me an **end date** (`August 1` — posts through the end of that day), a **number of times** (`8`), or `none` for open-ended."));
                return;
            }

            // "until August 1" means through the END of August 1, not its
            // midnight. The parser resolves a bare date to 00:00 local, which
            // as a bound would drop that whole day — invisible for a Weekly
            // reminder, but it silently costs a SixHourly one its last four
            // posts. Stretch a date-only answer to the last second of that
            // local day; an answer that named a time is taken literally.
            s.Draft.UntilUtc = r.HasTimeOfDay ? r.StartUtc : EndOfLocalDayUtc(r.StartUtc, tz);
            s.Draft.MaxOccurrences = null;
        }

        await PromptConfirmAsync(s);
    }

    // ─── Buttons ─────────────────────────────────────────────────────────────

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith(Prefix, StringComparison.Ordinal)) return;

        if (!_sessions.TryGetValue(component.User.Id, out var s))
        {
            await component.RespondAsync("That reminder setup has expired. Run `/reminder create` to start again.");
            return;
        }
        if (IsExpired(s))
        {
            _sessions.TryRemove(component.User.Id, out _);
            await ClearButtons(component, "⌛ Timed out. Run `/reminder create` again.");
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;

        var parts = component.Data.CustomId.Split(':'); // remwiz:<kind>:<arg?>
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
            _logger.LogError(ex, "Reminder wizard button {CustomId} failed", component.Data.CustomId);
            await SafeSend(s, "⚠️ Something went wrong on that step. Type `cancel` and run `/reminder create` to start over.");
        }
    }

    private async Task OnTimezoneButtonAsync(ReminderCreationSession s, SocketMessageComponent c, string arg)
    {
        if (!int.TryParse(arg, out var idx) || idx < 0 || idx >= EventTimeParser.CommonZones.Count)
        {
            await c.DeferAsync();
            return;
        }

        var (label, iana) = EventTimeParser.CommonZones[idx];
        await SaveTimeZoneAsync(s.Draft.CreatorId, iana);
        s.Draft.TimeZoneId = iana;
        s.Step = ReminderWizardStep.Title;

        await ClearButtons(c, $"Timezone set to **{label}** ({iana}).");
        await s.Dm.SendMessageAsync("🔔 What's the **title** of the reminder?");
    }

    private async Task OnRecurrenceButtonAsync(ReminderCreationSession s, SocketMessageComponent c, string arg)
    {
        if (arg == "none")
        {
            s.Draft.Frequency = null;
            s.Draft.UntilUtc = null;
            s.Draft.MaxOccurrences = null;
            await ClearButtons(c, "Repeats: **does not repeat**.");
            await PromptConfirmAsync(s);
            return;
        }

        ClanEventFrequency? freq = arg switch
        {
            "6h"       => ClanEventFrequency.SixHourly,
            "daily"    => ClanEventFrequency.Daily,
            "weekly"   => ClanEventFrequency.Weekly,
            "biweekly" => ClanEventFrequency.Biweekly,
            "monthly"  => ClanEventFrequency.Monthly,
            _          => null,
        };

        if (freq is null) { await c.DeferAsync(); return; }

        s.Draft.Frequency = freq;
        s.Step = ReminderWizardStep.RecurrenceUntil;
        await ClearButtons(c, $"Repeats: **{ClanReminderFrequency.Label(freq.Value)}**.");
        await s.Dm.SendMessageAsync(embed: Form("🔁 Until when?", "Give an **end date** (`August 1` — posts through the end of that day), a **number of times** (`8`), or `none` for open-ended."));
    }

    private async Task OnConfirmAsync(ReminderCreationSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.CreatorId, out _);

        try
        {
            await c.UpdateAsync(m =>
            {
                m.Content     = "⏳ Saving your reminder…";
                m.Embed       = null;
                m.Components   = new ComponentBuilder().Build();
                m.Attachments = new List<FileAttachment>();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reminder confirm ack (UpdateAsync) failed for {User}", s.Draft.CreatorId);
        }

        try
        {
            await PersistAsync(s.Draft);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persisting reminder '{Title}' failed for {User}", s.Draft.Title, s.Draft.CreatorId);
            await FinalizeConfirmAsync(c, "❌ Something went wrong saving the reminder. Please try `/reminder create` again.");
            return;
        }

        var verb = s.Draft.EditingReminderId is not null ? "updated" : "scheduled";
        await FinalizeConfirmAsync(c,
            $"✅ **Reminder {verb}!** It'll post to <#{s.Draft.ChannelId}> {EventTimeParser.Stamp(s.Draft.FirstFireUtc, 'R')} " +
            $"({EventTimeParser.Stamp(s.Draft.FirstFireUtc, 'F')}).");
    }

    /// <summary>Inserts a new ClanReminder, or updates the row being edited.</summary>
    private async Task PersistAsync(ReminderDraft d)
    {
        var tz = _time.ResolveZone(d.TimeZoneId);
        var firstLocal = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(d.FirstFireUtc, tz), DateTimeKind.Unspecified);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        ClanReminder row;
        if (d.EditingReminderId is int id)
        {
            row = await db.ClanReminders.FirstOrDefaultAsync(r => r.Id == id)
                  ?? throw new InvalidOperationException($"Reminder {id} vanished before edit save.");
        }
        else
        {
            row = new ClanReminder { CreatedAt = DateTime.UtcNow };
            db.ClanReminders.Add(row);
        }

        row.GuildId        = d.GuildId;
        row.ChannelId      = d.ChannelId;
        row.Title          = d.Title;
        row.Description    = d.Description ?? string.Empty;
        row.Url            = string.IsNullOrWhiteSpace(d.Url) ? null : d.Url;
        row.CreatorId      = d.CreatorId;
        row.CreatorName    = d.CreatorName;
        row.PingRoleIdsCsv = string.Join(",", d.PingRoleIds);
        row.PingUserIdsCsv = string.Join(",", d.PingUserIds);
        row.PingEveryone   = d.PingEveryone;
        row.PingHere       = d.PingHere;
        row.NextFireUtc    = d.FirstFireUtc;
        row.Status         = ClanReminderStatus.Scheduled;   // (re-)arm
        row.Frequency      = d.Frequency;
        row.TimeZoneId     = string.IsNullOrWhiteSpace(d.TimeZoneId) ? tz.Id : d.TimeZoneId;
        row.FirstFireLocal = firstLocal;
        row.NextOccurrenceIndex = 0;
        row.UntilUtc       = d.UntilUtc;
        row.MaxOccurrences = d.MaxOccurrences;
        row.ImageBytes     = d.ImageBytes;
        row.ImageFileName  = d.ImageFileName;
        row.CancelledAt    = null;

        // Post (or refresh) the "reminder scheduled" status card in the target
        // channel so members can see it exists — the reminder analogue of an
        // event's RSVP post. Best-effort; stamps row.AnnouncementMessageId, which
        // the SaveChanges below persists.
        await PostOrUpdateCardAsync(row, isEdit: d.EditingReminderId is not null);

        await db.SaveChangesAsync();

        _logger.LogInformation(
            "{Verb} reminder {Id} '{Title}' → channel {Channel}, first fire {Fire:o} UTC (freq={Freq})",
            d.EditingReminderId is null ? "Created" : "Updated", row.Id, row.Title, row.ChannelId, row.NextFireUtc, row.Frequency);
    }

    /// <summary>
    /// Posts a fresh status card to the reminder's channel (and, when editing,
    /// removes the previous card first so the details/image/channel always match).
    /// Never pings — posts with <see cref="AllowedMentions.None"/>. Best-effort;
    /// stamps <see cref="ClanReminder.AnnouncementMessageId"/> on success.
    /// </summary>
    private async Task PostOrUpdateCardAsync(ClanReminder row, bool isEdit)
    {
        try
        {
            var channel = _client.GetChannel(row.ChannelId) as IMessageChannel
                          ?? await _client.Rest.GetChannelAsync(row.ChannelId) as IMessageChannel;
            if (channel is null) return;

            // On edit, drop the old card so a changed image/channel/details can't
            // leave a stale one behind, then repost.
            if (isEdit && row.AnnouncementMessageId != 0)
            {
                try { await channel.DeleteMessageAsync(row.AnnouncementMessageId); }
                catch (Exception ex) { _logger.LogDebug(ex, "Couldn't delete prior reminder card {Msg}", row.AnnouncementMessageId); }
                row.AnnouncementMessageId = 0;
            }

            var embed = ReminderCard.Build(row, cancelled: false);
            IUserMessage posted;
            if (row.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(row.ImageFileName))
            {
                using var fa = new FileAttachment(new MemoryStream(row.ImageBytes), row.ImageFileName);
                posted = await channel.SendFileAsync(fa, text: null, embed: embed, allowedMentions: AllowedMentions.None);
            }
            else
            {
                posted = await channel.SendMessageAsync(text: null, embed: embed, allowedMentions: AllowedMentions.None);
            }

            row.AnnouncementMessageId = posted.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't post reminder status card for '{Title}'", row.Title);
        }
    }

    private async Task FinalizeConfirmAsync(SocketMessageComponent c, string content)
    {
        try
        {
            await c.ModifyOriginalResponseAsync(m =>
            {
                m.Content     = content;
                m.Embed       = null;
                m.Components   = new ComponentBuilder().Build();
                m.Attachments = new List<FileAttachment>();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to finalize reminder confirm message");
        }
    }

    private async Task OnCancelAsync(ReminderCreationSession s, SocketMessageComponent c)
    {
        _sessions.TryRemove(s.Draft.CreatorId, out _);
        await ClearButtons(c, "❌ Cancelled. Nothing was saved.");
    }

    // ─── Prompts ───────────────────────────────────────────────────────────

    private async Task PromptTimezoneAsync(ReminderCreationSession s)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < EventTimeParser.CommonZones.Count; i++)
        {
            var (label, _) = EventTimeParser.CommonZones[i];
            builder.WithButton(label, $"{Prefix}tz:{i}", ButtonStyle.Secondary, row: i / 5);
        }

        await s.Dm.SendMessageAsync(
            "🔔 **New reminder!** First — what's your **timezone**? Tap one below, or just type it (e.g. `America/Chicago`).",
            components: builder.Build());
    }

    private async Task PromptRecurrenceAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.Recurrence;
        // Six buttons: an action row holds at most five, so Monthly wraps to row 1.
        var builder = new ComponentBuilder()
            .WithButton("Does not repeat", $"{Prefix}rec:none",     ButtonStyle.Secondary, row: 0)
            .WithButton("Every 6 hours",   $"{Prefix}rec:6h",       ButtonStyle.Secondary, row: 0)
            .WithButton("Daily",           $"{Prefix}rec:daily",    ButtonStyle.Secondary, row: 0)
            .WithButton("Weekly",          $"{Prefix}rec:weekly",   ButtonStyle.Secondary, row: 0)
            .WithButton("Biweekly",        $"{Prefix}rec:biweekly", ButtonStyle.Secondary, row: 0)
            .WithButton("Monthly",         $"{Prefix}rec:monthly",  ButtonStyle.Secondary, row: 1);

        await s.Dm.SendMessageAsync(embed: Form("🔁 Does this reminder repeat?",
            "Pick an option. A repeating reminder re-posts at the same local time each cycle."), components: builder.Build());
    }

    private async Task PromptConfirmAsync(ReminderCreationSession s)
    {
        s.Step = ReminderWizardStep.Confirm;
        var d = s.Draft;

        var embed = new EmbedBuilder()
            .WithTitle(d.EditingReminderId is null ? "📋 Confirm reminder" : "📋 Confirm changes")
            .WithColor(Color.Blue)
            .AddField("Title", d.Title)
            .AddField("Posts", $"{EventTimeParser.Stamp(d.FirstFireUtc, 'F')} ({EventTimeParser.Stamp(d.FirstFireUtc, 'R')})")
            .AddField("Channel", $"<#{d.ChannelId}>", inline: true)
            .AddField("Repeats", DescribeRecurrence(d), inline: true)
            .AddField("Pings", DescribePings(d), inline: true);

        if (!string.IsNullOrWhiteSpace(d.Url))
            embed.AddField("Link", d.Url);
        if (!string.IsNullOrWhiteSpace(d.Description))
            embed.AddField("Description", Truncate(d.Description, 1024));

        var zoneLabel = string.IsNullOrWhiteSpace(d.TimeZoneId) ? _config.EventDefaultTimeZone : d.TimeZoneId;
        embed.WithFooter($"Read in {zoneLabel} • shown in your local time • /timezone to change");

        if (!string.IsNullOrWhiteSpace(d.ImageFileName))
            embed.WithThumbnailUrl($"attachment://{d.ImageFileName}");

        var buttons = new ComponentBuilder()
            .WithButton(d.EditingReminderId is null ? "Schedule it" : "Save changes", $"{Prefix}confirm", ButtonStyle.Success)
            .WithButton("Cancel", $"{Prefix}cancel", ButtonStyle.Danger);

        if (d.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(d.ImageFileName))
        {
            using var fa = new FileAttachment(new MemoryStream(d.ImageBytes), d.ImageFileName);
            await s.Dm.SendFileAsync(fa, text: "Here's how it'll look — good to go?", embed: embed.Build(), components: buttons.Build());
        }
        else
        {
            await s.Dm.SendMessageAsync("Here's how it'll look — good to go?", embed: embed.Build(), components: buttons.Build());
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

    private string DescribePings(ReminderDraft d)
    {
        var parts = new List<string>();
        parts.AddRange(d.PingRoleIds.Select(id => $"<@&{id}>"));
        parts.AddRange(d.PingUserIds.Select(id => $"<@{id}>"));
        if (d.PingHere) parts.Add("@here");
        if (d.PingEveryone) parts.Add("@everyone");
        return parts.Count == 0 ? "No ping" : string.Join(" ", parts);
    }

    /// <summary>
    /// Last second (UTC) of the local day <paramref name="instantUtc"/> falls in.
    /// Used to turn a date-only recurrence bound into "through that day".
    /// </summary>
    private static DateTime EndOfLocalDayUtc(DateTime instantUtc, TimeZoneInfo tz)
    {
        var local   = TimeZoneInfo.ConvertTimeFromUtc(instantUtc, tz);
        var endLocal = DateTime.SpecifyKind(local.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Unspecified);
        return tz.IsInvalidTime(endLocal)
            ? instantUtc.AddDays(1).AddSeconds(-1)
            : TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
    }

    private static string DescribeRecurrence(ReminderDraft d)
    {
        if (d.Frequency is null) return "Does not repeat";
        var bound = d.MaxOccurrences is int n ? $", {n} times"
                  : d.UntilUtc is DateTime u ? $", until {EventTimeParser.Stamp(u, 'd')}"
                  : ", ongoing";
        return $"{ClanReminderFrequency.Label(d.Frequency.Value)}{bound}";
    }

    private static bool IsSkip(string text) =>
        text.Equals("skip", StringComparison.OrdinalIgnoreCase)
     || text.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>"keep" only means something in edit mode — ignored on a fresh create.</summary>
    private static bool IsKeep(ReminderCreationSession s, string text) =>
        s.Draft.EditingReminderId is not null && text.Equals("keep", StringComparison.OrdinalIgnoreCase);

    private static List<ulong> SplitIds(string csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => ulong.TryParse(p, out var n) ? n : 0UL)
            .Where(n => n != 0)
            .ToList();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static readonly Color FormColor = new(0x5865F2);

    private static Embed Form(string title, string? body = null)
    {
        var eb = new EmbedBuilder()
            .WithColor(FormColor)
            .WithTitle(title)
            .WithFooter("Reply in this DM • type \"cancel\" to quit • times out after 15 min");
        if (!string.IsNullOrWhiteSpace(body)) eb.WithDescription(body);
        return eb.Build();
    }

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
                            "⌛ Reminder setup timed out",
                            "Looks like you stepped away — I've cancelled this setup and **nothing was saved**. Run `/reminder create` whenever you're ready."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send reminder-wizard timeout DM to {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Reminder wizard idle sweep failed"); }
    }

    private static bool IsExpired(ReminderCreationSession s) =>
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

    private async Task SafeSend(ReminderCreationSession s, string content)
    {
        try { await s.Dm.SendMessageAsync(content); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send reminder-wizard DM to {User}", s.Draft.CreatorId); }
    }

    private async Task SafeSendEmbed(ReminderCreationSession s, Embed embed)
    {
        try { await s.Dm.SendMessageAsync(embed: embed); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send reminder-wizard DM to {User}", s.Draft.CreatorId); }
    }
}
