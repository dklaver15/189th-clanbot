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
/// Reusable event templates, reached via the <c>/event template</c> subcommand
/// group (dispatched here by <see cref="EventCommandHandler"/>):
///   • <c>save</c>   — CPT+ (EventTemplateManageMinRank): a short DM wizard that
///                     captures the boilerplate of a recurring kind of event
///                     (name, title, length, description, banner, attendee cap).
///   • <c>list</c>   — event-creators: show the saved templates.
///   • <c>use</c>    — event-creators: pick a template, then supply only the
///                     date/time (a modal) and the host (a member picker); the
///                     rest is filled from the template and published as a normal
///                     one-off event via <see cref="IEventPublisher"/>.
///   • <c>delete</c> — CPT+: remove a template.
///
/// Save runs in DMs (free-text + an image upload, like the creation wizard). Use
/// runs entirely in-guild and ephemeral, because picking a host needs guild
/// context for a member select — something a DM doesn't have.
/// </summary>
public sealed class EventTemplateHandler
{
    private const string Prefix = "evttpl:";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Color FormColor = new(0x5865F2);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly BotConfig _config;
    private readonly EventTimeParser _time;
    private readonly ILogger<EventTemplateHandler> _logger;
    private readonly System.Threading.Timer _idleSweep;

    // ── Save DM wizard sessions (keyed by user id) ──
    private enum SaveStep { Name, Title, Duration, Description, Image, MaxParticipants, Confirm }

    private sealed class SaveSession
    {
        public ulong UserId;
        public IDMChannel Dm = null!;
        public ulong GuildId;
        public SaveStep Step = SaveStep.Name;
        public DateTime LastActivityAt;

        public string Name = string.Empty;
        public string Title = string.Empty;
        public int DurationMinutes;
        public string Description = string.Empty;
        public byte[]? ImageBytes;
        public string? ImageFileName;
        public int? MaxParticipants;
    }

    private readonly ConcurrentDictionary<ulong, SaveSession> _saveSessions = new();

    // ── Edit DM sessions (numbered field menu; staged, applied on "done") ──
    private enum EditField { Menu, Name, Title, Duration, Description, Image, MaxParticipants }

    private sealed class EditSession
    {
        public ulong UserId;
        public IDMChannel Dm = null!;
        public ulong GuildId;
        public int TemplateId;
        public EditField Step = EditField.Menu;
        public DateTime LastActivityAt;

        // Working copy, pre-loaded from the template.
        public string Name = string.Empty;
        public string Title = string.Empty;
        public int DurationMinutes;
        public string Description = string.Empty;
        public int? MaxParticipants;

        // Image is staged separately: only touched if the user changes it.
        public bool HasImage;      // did the template have a banner when we started
        public bool ImageChanged;  // did the user stage an image change this session
        public bool ImageCleared;  // ...and was it a removal
        public byte[]? ImageBytes;
        public string? ImageFileName;
    }

    private readonly ConcurrentDictionary<ulong, EditSession> _editSessions = new();

    public EventTemplateHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        IOptions<BotConfig> config,
        EventTimeParser time,
        ILogger<EventTemplateHandler> logger)
    {
        _services = services;
        _client   = client;
        _config   = config.Value;
        _time     = time;
        _logger   = logger;

        _idleSweep = new System.Threading.Timer(
            _ => _ = SweepIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public void Register(DiscordSocketClient client)
    {
        client.MessageReceived    += OnSaveDmAsync;
        client.MessageReceived    += OnEditDmAsync;
        client.ButtonExecuted     += OnButtonAsync;
        client.SelectMenuExecuted += OnSelectAsync;
        client.ModalSubmitted     += OnModalAsync;
    }

    // ─── Dispatch (called by EventCommandHandler) ──────────────────────────

    public async Task HandleTemplateAsync(SocketSlashCommand command, string sub)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.GuildId is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }
        var guildUser = command.User as SocketGuildUser;
        if (guildUser is null)
        {
            await command.FollowupAsync("This command can only be used in a server.", ephemeral: true);
            return;
        }

        switch (sub)
        {
            case "save":   await StartSaveAsync(command, guildUser);   break;
            case "list":   await ShowListAsync(command, guildUser);    break;
            case "use":    await StartUseAsync(command, guildUser);    break;
            case "edit":   await StartEditAsync(command, guildUser);   break;
            case "delete": await StartDeleteAsync(command, guildUser); break;
            default:       await command.FollowupAsync("Unknown template subcommand.", ephemeral: true); break;
        }
    }

    // ─── /event template save ──────────────────────────────────────────────

    private async Task StartSaveAsync(SocketSlashCommand command, SocketGuildUser user)
    {
        if (!HasManagePermission(user))
        {
            await command.FollowupAsync(
                $"❌ Saving templates is restricted to **{_config.EventTemplateManageMinRank} and above**.",
                ephemeral: true);
            return;
        }

        var (dm, dmFailure) = await DmGuard.TryOpenAsync(user);
        if (dm is null)
        {
            _logger.LogInformation("Could not open DM with {User} for template save: {Failure}",
                user.Id, dmFailure);
            await command.FollowupAsync(DmGuard.AdviceFor(dmFailure), ephemeral: true);
            return;
        }

        var session = new SaveSession
        {
            UserId         = user.Id,
            Dm             = dm,
            GuildId        = command.GuildId!.Value,
            Step           = SaveStep.Name,
            LastActivityAt = DateTime.UtcNow,
        };
        _saveSessions[user.Id] = session;

        try
        {
            await dm.SendMessageAsync(embed: Form("🧩 New event template",
                "What should this template be **called**? This is the name officers pick from later — e.g. `Friday Night Ops`."));
            await command.FollowupAsync("📬 Check your DMs — I'll walk you through saving the template there.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not send opening template-save DM to {User}", user.Id);
            _saveSessions.TryRemove(user.Id, out _);
            await command.FollowupAsync(
                "I couldn't DM you. Enable **Direct Messages** from server members (Privacy Settings) and try again.",
                ephemeral: true);
        }
    }

    private async Task OnSaveDmAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_saveSessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsExpired(s))
        {
            _saveSessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, Form("⌛ Template setup timed out",
                "That setup expired from inactivity and **nothing was saved**. Run `/event template save` to start over."));
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _saveSessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, Form("Cancelled", "No template was saved. Run `/event template save` to start again."));
            return;
        }

        try
        {
            switch (s.Step)
            {
                case SaveStep.Name:            await HandleNameAsync(s, text);            break;
                case SaveStep.Title:           await HandleTitleAsync(s, text);           break;
                case SaveStep.Duration:        await HandleDurationAsync(s, text);        break;
                case SaveStep.Description:     await HandleDescriptionAsync(s, text);     break;
                case SaveStep.Image:           await HandleImageAsync(s, message, text);  break;
                case SaveStep.MaxParticipants: await HandleMaxParticipantsAsync(s, text); break;
                // Confirm is a button step — ignore stray text.
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template save step {Step} failed for {User}", s.Step, message.Author.Id);
            _saveSessions.TryRemove(message.Author.Id, out _);
            await SafeSend(s, Form("Something went wrong", "Run `/event template save` to start over."));
        }
    }

    private async Task HandleNameAsync(SaveSession s, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await s.Dm.SendMessageAsync(embed: Form("🧩 Template name", "Give the template a name — e.g. `Friday Night Ops`."));
            return;
        }
        if (text.Length > 80)
        {
            await s.Dm.SendMessageAsync(embed: Form("Name too long", "Keep it under 80 characters — try a shorter one."));
            return;
        }

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var dupe = await db.ClanEventTemplates
                .Where(t => t.GuildId == s.GuildId)
                .AnyAsync(t => t.Name.ToLower() == text.ToLower());
            if (dupe)
            {
                await s.Dm.SendMessageAsync(embed: Form("🧩 That name's taken",
                    $"A template called **{text}** already exists. Pick a different name, or delete the old one with `/event template delete` first."));
                return;
            }
        }

        s.Name = text;
        s.Step = SaveStep.Title;
        await s.Dm.SendMessageAsync(embed: Form("🎯 Event title",
            "What **title** should events made from this template have? (This is what shows on the post — it can match the template name or differ.)"));
    }

    private async Task HandleTitleAsync(SaveSession s, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await s.Dm.SendMessageAsync(embed: Form("🎯 Event title", "Give it a title — e.g. `Friday Night Ops`."));
            return;
        }
        if (text.Length > 100)
        {
            await s.Dm.SendMessageAsync(embed: Form("Title too long", "Keep it under 100 characters."));
            return;
        }

        s.Title = text;
        s.Step = SaveStep.Duration;
        await s.Dm.SendMessageAsync(embed: Form("⏱️ How long does it run?",
            "Default length for events from this template — e.g. `2 hours`, `90 minutes`, `1h 30m`."));
    }

    private async Task HandleDurationAsync(SaveSession s, string text)
    {
        var minutes = ParseDurationMinutes(text);
        if (minutes is null or <= 0)
        {
            await s.Dm.SendMessageAsync(embed: Form("⏱️ Need a length",
                "Tell me a duration like `2 hours`, `90 minutes`, or `1h 30m`."));
            return;
        }

        s.DurationMinutes = minutes.Value;
        s.Step = SaveStep.Description;
        await s.Dm.SendMessageAsync(embed: Form("📝 Description",
            "Add a default description for events from this template, or type `skip`."));
    }

    private async Task HandleDescriptionAsync(SaveSession s, string text)
    {
        // Cap at Discord's embed-description limit (4096) — events created from
        // this template render the description via WithDescription. The confirm
        // preview truncates further (1024) since it shows it as an embed field.
        if (text.Length > 4096
         && !text.Equals("skip", StringComparison.OrdinalIgnoreCase)
         && !text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Description too long",
                "Keep it under 4096 characters — trim it down, or type `skip`."));
            return;
        }

        s.Description = text.Equals("skip", StringComparison.OrdinalIgnoreCase)
                     || text.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : text;

        s.Step = SaveStep.Image;
        await s.Dm.SendMessageAsync(embed: Form("🖼️ Banner image",
            "Drag an image into this DM, or paste a GIF/image link (Tenor, Giphy, or direct — PNG/JPG/GIF/WebP, max 8 MB). Or type `skip`."));
    }

    private async Task HandleImageAsync(SaveSession s, SocketMessage message, string text)
    {
        if (text.Equals("skip", StringComparison.OrdinalIgnoreCase)
         || text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            s.ImageBytes = null;
            s.ImageFileName = null;
            await AdvanceToMaxParticipantsAsync(s);
            return;
        }

        var att = message.Attachments.FirstOrDefault();
        if (att is null)
        {
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await s.Dm.SendMessageAsync("⏳ Fetching that image…");
                var res = await EventImageFetcher.FromUrlAsync(text);
                if (!res.Ok)
                {
                    await s.Dm.SendMessageAsync(embed: Form("🖼️ Couldn't use that link",
                        $"{res.Error} Try another link, drag the file in, or type `skip`."));
                    return;
                }
                s.ImageFileName = res.FileName;
                s.ImageBytes    = EventImage.Downscale(res.Bytes!, res.FileName);
                await AdvanceToMaxParticipantsAsync(s);
                return;
            }

            await s.Dm.SendMessageAsync(embed: Form("🖼️ Banner image",
                "Drag an image into this DM, paste a GIF/image link, or type `skip`."));
            return;
        }

        var looksImage = (att.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                      || EventImage.IsAllowedExtension(att.Filename);
        if (!looksImage)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Not an image",
                "That doesn't look like a PNG/JPG/GIF/WebP. Try another, or type `skip`."));
            return;
        }
        if (att.Size > EventImage.MaxBytes)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image too large",
                $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB — try a smaller one, or type `skip`."));
            return;
        }

        byte[] bytes;
        try { bytes = await Http.GetByteArrayAsync(att.Url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download template image for {User}", s.UserId);
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Download failed", "Couldn't download that image. Try again, or type `skip`."));
            return;
        }
        if (bytes.Length > EventImage.MaxBytes)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image too large",
                $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB — try a smaller one, or type `skip`."));
            return;
        }

        var imgName = EventImage.Sanitize(att.Filename);
        s.ImageFileName = imgName;
        s.ImageBytes    = EventImage.Downscale(bytes, imgName);
        await AdvanceToMaxParticipantsAsync(s);
    }

    private async Task AdvanceToMaxParticipantsAsync(SaveSession s)
    {
        s.Step = SaveStep.MaxParticipants;
        await s.Dm.SendMessageAsync(embed: Form("👥 Default attendee limit?",
            "Type a **max number** on the Going list (extra sign-ups waitlist automatically), or `none` for no limit."));
    }

    private async Task HandleMaxParticipantsAsync(SaveSession s, string text)
    {
        text = text.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)
         || text.Equals("skip", StringComparison.OrdinalIgnoreCase)
         || text.Equals("unlimited", StringComparison.OrdinalIgnoreCase)
         || text == "0")
        {
            s.MaxParticipants = null;
        }
        else if (int.TryParse(text, out var n) && n > 0)
        {
            s.MaxParticipants = n;
        }
        else
        {
            await s.Dm.SendMessageAsync(embed: Form("👥 Need a number",
                "Give me a whole number greater than 0 (e.g. `16`), or type `none` for no limit."));
            return;
        }

        await PromptConfirmAsync(s);
    }

    private async Task PromptConfirmAsync(SaveSession s)
    {
        s.Step = SaveStep.Confirm;

        var embed = new EmbedBuilder()
            .WithTitle("🧩 Save this template?")
            .WithColor(Color.Blue)
            .AddField("Template name", s.Name)
            .AddField("Event title", s.Title)
            .AddField("Default length", FormatDuration(s.DurationMinutes), inline: true)
            .AddField("Attendee limit", s.MaxParticipants is int cap ? $"{cap} max (waitlist past that)" : "No limit", inline: true)
            .AddField("Banner", string.IsNullOrWhiteSpace(s.ImageFileName) ? "None" : "Set", inline: true);

        if (!string.IsNullOrWhiteSpace(s.Description))
            embed.AddField("Description", Truncate(s.Description, 1024));

        if (!string.IsNullOrWhiteSpace(s.ImageFileName))
            embed.WithThumbnailUrl($"attachment://{s.ImageFileName}");

        var buttons = new ComponentBuilder()
            .WithButton("Save template", $"{Prefix}save:confirm", ButtonStyle.Success)
            .WithButton("Cancel",        $"{Prefix}save:cancel",  ButtonStyle.Danger);

        if (s.ImageBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(s.ImageFileName))
        {
            using var fa = new FileAttachment(new MemoryStream(s.ImageBytes), s.ImageFileName);
            await s.Dm.SendFileAsync(fa, text: "Here's the template — save it?", embed: embed.Build(), components: buttons.Build());
        }
        else
        {
            await s.Dm.SendMessageAsync("Here's the template — save it?", embed: embed.Build(), components: buttons.Build());
        }
    }

    // ─── /event template list ──────────────────────────────────────────────

    private async Task ShowListAsync(SocketSlashCommand command, SocketGuildUser user)
    {
        if (!HasUsePermission(user))
        {
            await command.FollowupAsync(
                $"❌ Templates are available to **{_config.EventCommandMinRank} and above**.", ephemeral: true);
            return;
        }

        List<ClanEventTemplate> templates;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            templates = await db.ClanEventTemplates
                .Where(t => t.GuildId == command.GuildId!.Value)
                .OrderBy(t => t.Name)
                .ToListAsync();
        }

        if (templates.Count == 0)
        {
            await command.FollowupAsync(
                "No event templates yet. A CPT+ officer can create one with `/event template save`.", ephemeral: true);
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle("🧩 Event templates")
            .WithColor(FormColor)
            .WithFooter("Use one with /event template use");

        foreach (var t in templates.Take(25))
        {
            var bits = new List<string> { $"⏱️ {FormatDuration(t.DurationMinutes)}" };
            bits.Add(t.MaxParticipants is int c ? $"👥 {c} max" : "👥 no limit");
            if (!string.IsNullOrWhiteSpace(t.ImageFileName)) bits.Add("🖼️ banner");
            var body = $"{string.Join(" · ", bits)}\n*{Truncate(string.IsNullOrWhiteSpace(t.Description) ? t.Title : t.Description, 150)}*";
            embed.AddField(Truncate(t.Name, 100), body);
        }

        await command.FollowupAsync(embed: embed.Build(), ephemeral: true);
    }

    // ─── /event template use ───────────────────────────────────────────────

    private async Task StartUseAsync(SocketSlashCommand command, SocketGuildUser user)
    {
        if (!HasUsePermission(user))
        {
            await command.FollowupAsync(
                $"❌ Creating events is restricted to **{_config.EventCommandMinRank} and above**.", ephemeral: true);
            return;
        }

        List<ClanEventTemplate> templates;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            templates = await db.ClanEventTemplates
                .Where(t => t.GuildId == command.GuildId!.Value)
                .OrderBy(t => t.Name)
                .Take(25)
                .ToListAsync();
        }

        if (templates.Count == 0)
        {
            await command.FollowupAsync(
                "No event templates yet. A CPT+ officer can create one with `/event template save`.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}use:pick")
            .WithPlaceholder("Choose a template")
            .WithMinValues(1).WithMaxValues(1);

        foreach (var t in templates)
            menu.AddOption(Truncate(t.Name, 100), t.Id.ToString(),
                Truncate($"{t.Title} · {FormatDuration(t.DurationMinutes)}", 100));

        await command.FollowupAsync("Which template would you like to use?",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    // ─── /event template delete ────────────────────────────────────────────

    private async Task StartDeleteAsync(SocketSlashCommand command, SocketGuildUser user)
    {
        if (!HasManagePermission(user))
        {
            await command.FollowupAsync(
                $"❌ Deleting templates is restricted to **{_config.EventTemplateManageMinRank} and above**.", ephemeral: true);
            return;
        }

        List<ClanEventTemplate> templates;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            templates = await db.ClanEventTemplates
                .Where(t => t.GuildId == command.GuildId!.Value)
                .OrderBy(t => t.Name)
                .Take(25)
                .ToListAsync();
        }

        if (templates.Count == 0)
        {
            await command.FollowupAsync("There are no templates to delete.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}del:pick")
            .WithPlaceholder("Choose a template to delete")
            .WithMinValues(1).WithMaxValues(1);

        foreach (var t in templates)
            menu.AddOption(Truncate(t.Name, 100), t.Id.ToString(), Truncate(t.Title, 100));

        await command.FollowupAsync("Which template would you like to delete?",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    // ─── Buttons ────────────────────────────────────────────────────────────

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        var cid = component.Data.CustomId;
        if (!cid.StartsWith(Prefix, StringComparison.Ordinal)) return;

        try
        {
            if (cid == $"{Prefix}save:confirm") { await SaveConfirmAsync(component); return; }
            if (cid == $"{Prefix}save:cancel")
            {
                _saveSessions.TryRemove(component.User.Id, out _);
                await component.UpdateAsync(m => { m.Content = "❌ Cancelled. Nothing was saved."; m.Embed = null; m.Components = Empty(); m.Attachments = new List<FileAttachment>(); });
                return;
            }

            // Use flow: host chosen via button (self / none). evttpl:usehostself:<id>:<ticks>
            if (cid.StartsWith($"{Prefix}usehostself:", StringComparison.Ordinal)
             || cid.StartsWith($"{Prefix}usehostnone:", StringComparison.Ordinal))
            {
                var self = cid.StartsWith($"{Prefix}usehostself:", StringComparison.Ordinal);
                var parts = cid.Split(':'); // evttpl:usehostX:<templateId>:<ticks>
                if (parts.Length != 4
                 || !int.TryParse(parts[2], out var tid)
                 || !long.TryParse(parts[3], out var ticks)) { await component.DeferAsync(); return; }

                var hostId = self ? component.User.Id : (ulong?)null;
                await FinishUseAsync(component, tid, new DateTime(ticks, DateTimeKind.Utc), hostId);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template button {Cid} failed", cid);
        }
    }

    private async Task SaveConfirmAsync(SocketMessageComponent component)
    {
        if (!_saveSessions.TryRemove(component.User.Id, out var s))
        {
            await component.UpdateAsync(m => { m.Content = "That template setup has expired. Run `/event template save` again."; m.Embed = null; m.Components = Empty(); m.Attachments = new List<FileAttachment>(); });
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Re-check uniqueness at save time (another officer could have taken the name).
        var dupe = await db.ClanEventTemplates
            .Where(t => t.GuildId == s.GuildId)
            .AnyAsync(t => t.Name.ToLower() == s.Name.ToLower());
        if (dupe)
        {
            await component.UpdateAsync(m =>
            {
                m.Content = $"A template called **{s.Name}** already exists now — nothing was saved. Run `/event template save` with a different name.";
                m.Embed = null; m.Components = Empty(); m.Attachments = new List<FileAttachment>();
            });
            return;
        }

        var creatorName = (component.User as SocketGuildUser)?.DisplayName ?? component.User.GlobalName ?? component.User.Username;
        db.ClanEventTemplates.Add(new ClanEventTemplate
        {
            GuildId         = s.GuildId,
            Name            = s.Name,
            Title           = s.Title,
            Description     = s.Description,
            DurationMinutes = s.DurationMinutes,
            MaxParticipants = s.MaxParticipants,
            ImageBytes      = s.ImageBytes,
            ImageFileName   = s.ImageFileName,
            CreatedById     = component.User.Id,
            CreatedByName   = creatorName,
            CreatedAt       = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        await component.UpdateAsync(m =>
        {
            m.Content = $"✅ Saved template **{s.Name}**. Anyone who can create events can now use it with `/event template use`.";
            m.Embed = null; m.Components = Empty(); m.Attachments = new List<FileAttachment>();
        });
    }

    // ─── Select menus ────────────────────────────────────────────────────────

    private async Task OnSelectAsync(SocketMessageComponent component)
    {
        var cid = component.Data.CustomId;
        if (!cid.StartsWith(Prefix, StringComparison.Ordinal)) return;

        try
        {
            if (cid == $"{Prefix}use:pick")  { await OnUsePickAsync(component);    return; }
            if (cid == $"{Prefix}edit:pick") { await OnEditPickAsync(component);   return; }
            if (cid == $"{Prefix}del:pick")  { await OnDeletePickAsync(component); return; }

            // Host chosen via the member picker. evttpl:usehost:<templateId>:<ticks>
            if (cid.StartsWith($"{Prefix}usehost:", StringComparison.Ordinal))
            {
                var parts = cid.Split(':'); // evttpl:usehost:<templateId>:<ticks>
                if (parts.Length != 4
                 || !int.TryParse(parts[2], out var tid)
                 || !long.TryParse(parts[3], out var ticks)
                 || !ulong.TryParse(component.Data.Values.FirstOrDefault(), out var hostId))
                {
                    await component.UpdateAsync(m => { m.Content = "Something went wrong reading that selection."; m.Components = Empty(); });
                    return;
                }
                await FinishUseAsync(component, tid, new DateTime(ticks, DateTimeKind.Utc), hostId);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template select {Cid} failed", cid);
        }
    }

    private async Task OnUsePickAsync(SocketMessageComponent component)
    {
        if (component.User is not SocketGuildUser gu || !HasUsePermission(gu))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to use templates."; m.Components = Empty(); });
            return;
        }
        if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var templateId))
        {
            await component.UpdateAsync(m => { m.Content = "Couldn't read that selection."; m.Components = Empty(); });
            return;
        }

        string name;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var t = await db.ClanEventTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
            if (t is null || t.GuildId != gu.Guild.Id)
            {
                await component.UpdateAsync(m => { m.Content = "That template no longer exists."; m.Components = Empty(); });
                return;
            }
            name = t.Name;
        }

        var modal = new ModalBuilder()
            .WithTitle(Truncate($"Use: {name}", 45))
            .WithCustomId($"{Prefix}usewhen:{templateId}")
            .AddTextInput("When is it?", "when", TextInputStyle.Short,
                placeholder: "e.g. 7pm Friday, tomorrow 8pm, June 20 7pm", required: true, maxLength: 100);

        await component.RespondWithModalAsync(modal.Build());
    }

    private async Task OnDeletePickAsync(SocketMessageComponent component)
    {
        if (component.User is not SocketGuildUser gu || !HasManagePermission(gu))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to delete templates."; m.Components = Empty(); });
            return;
        }
        if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var templateId))
        {
            await component.UpdateAsync(m => { m.Content = "Couldn't read that selection."; m.Components = Empty(); });
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var t = await db.ClanEventTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
        if (t is null || t.GuildId != gu.Guild.Id)
        {
            await component.UpdateAsync(m => { m.Content = "That template no longer exists."; m.Components = Empty(); });
            return;
        }

        var name = t.Name;
        db.ClanEventTemplates.Remove(t);
        await db.SaveChangesAsync();

        await component.UpdateAsync(m => { m.Content = $"🗑️ Deleted template **{name}**."; m.Components = Empty(); });
    }

    // ─── Modal (the "when" step of the use flow) ──────────────────────────────

    private async Task OnModalAsync(SocketModal modal)
    {
        var cid = modal.Data.CustomId;
        if (!cid.StartsWith($"{Prefix}usewhen:", StringComparison.Ordinal)) return;

        try
        {
            if (!int.TryParse(cid[(cid.LastIndexOf(':') + 1)..], out var templateId))
            {
                await modal.RespondAsync("Couldn't read that template.", ephemeral: true);
                return;
            }
            if (modal.User is not SocketGuildUser gu || !HasUsePermission(gu))
            {
                await modal.RespondAsync("You don't have permission to use templates.", ephemeral: true);
                return;
            }

            ClanEventTemplate? t;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                t = await db.ClanEventTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
            }
            if (t is null || t.GuildId != gu.Guild.Id)
            {
                await modal.RespondAsync("That template no longer exists.", ephemeral: true);
                return;
            }

            var whenText = modal.Data.Components.FirstOrDefault(c => c.CustomId == "when")?.Value?.Trim() ?? string.Empty;
            var tz = _time.ResolveZone(await ResolveZoneIdAsync(gu.Id));
            var r  = _time.ParseStart(whenText, tz);
            if (!r.Success)
            {
                await modal.RespondAsync($"⚠️ {(string.IsNullOrWhiteSpace(r.Error) ? "I couldn't read that time." : r.Error)} Run `/event template use` and try again.", ephemeral: true);
                return;
            }
            if (!r.HasTimeOfDay)
            {
                await modal.RespondAsync("⚠️ I got a date but no time of day — include a time too, like `June 20 at 7pm`. Run `/event template use` and try again.", ephemeral: true);
                return;
            }
            if (r.StartUtc <= DateTime.UtcNow)
            {
                await modal.RespondAsync("⚠️ That's in the past — pick a future date & time. Run `/event template use` and try again.", ephemeral: true);
                return;
            }

            var ticks = r.StartUtc.Ticks;
            var hostMenu = new SelectMenuBuilder()
                .WithCustomId($"{Prefix}usehost:{templateId}:{ticks}")
                .WithType(ComponentType.UserSelect)
                .WithPlaceholder("Choose the host")
                .WithMinValues(1).WithMaxValues(1);

            var buttons = new ComponentBuilder()
                .WithSelectMenu(hostMenu)
                .WithButton("I'll host it", $"{Prefix}usehostself:{templateId}:{ticks}", ButtonStyle.Secondary, row: 1)
                .WithButton("No host yet",  $"{Prefix}usehostnone:{templateId}:{ticks}", ButtonStyle.Secondary, row: 1);

            await modal.RespondAsync(
                $"📅 **{t.Title}** on {EventTimeParser.Stamp(r.StartUtc, 'F')} ({EventTimeParser.Stamp(r.StartUtc, 'R')}).\n\nWho's **hosting**? Pick a member, or choose below.",
                components: buttons.Build(), ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template use modal failed ({Cid})", cid);
            try { await modal.RespondAsync("Something went wrong. Run `/event template use` to try again.", ephemeral: true); } catch { }
        }
    }

    // ─── Finish: publish the templated event ──────────────────────────────────

    private async Task FinishUseAsync(SocketMessageComponent component, int templateId, DateTime startUtc, ulong? hostId)
    {
        if (component.User is not SocketGuildUser gu || !HasUsePermission(gu))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to use templates."; m.Components = Empty(); });
            return;
        }

        ClanEventTemplate? t;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            t = await db.ClanEventTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
        }
        if (t is null || t.GuildId != gu.Guild.Id)
        {
            await component.UpdateAsync(m => { m.Content = "That template no longer exists."; m.Components = Empty(); });
            return;
        }

        // Ack first — publishing posts to #events and can exceed the 3s window.
        try { await component.UpdateAsync(m => { m.Content = "⏳ Creating your event…"; m.Components = Empty(); }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Template use ack failed"); }

        var draft = new EventDraft
        {
            GuildId         = gu.Guild.Id,
            OrganizerId     = component.User.Id,
            OrganizerName   = gu.DisplayName,
            TimeZoneId      = await ResolveZoneIdAsync(gu.Id),
            Title           = t.Title,
            Description     = t.Description,
            StartUtc        = startUtc,
            EndUtc          = startUtc.AddMinutes(t.DurationMinutes),
            ImageBytes      = t.ImageBytes,
            ImageFileName   = t.ImageFileName,
            MaxParticipants = t.MaxParticipants,
            Frequency       = null,
            HostId          = hostId,
        };

        try
        {
            using var scope = _services.CreateScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
            await publisher.PublishOneOffAsync(draft);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing templated event '{Title}' failed for {User}", t.Title, component.User.Id);
            await Finalize(component, "❌ Something went wrong creating the event. Please try `/event template use` again.");
            return;
        }

        if (hostId is ulong hid && hid != component.User.Id)
            await NotifyHostAsync(gu.Guild.Id, hid, t.Title, startUtc, gu.DisplayName);

        var postChannelId = _config.GetEventPostChannelId();
        var channelMention = postChannelId != 0 ? $"<#{postChannelId}>" : "the events channel";
        var hostNote = hostId is null ? "" : $" Host: <@{hostId}>.";
        await Finalize(component, $"✅ **{t.Title}** created for {EventTimeParser.Stamp(startUtc, 'F')} — posted to {channelMention}.{hostNote}");
    }

    private async Task NotifyHostAsync(ulong guildId, ulong hostId, string title, DateTime startUtc, string setterName)
    {
        try
        {
            var host = (_client.GetGuild(guildId)?.GetUser(hostId) as IUser) ?? _client.GetUser(hostId);
            if (host is null) return;

            var embed = new EmbedBuilder()
                .WithColor(FormColor)
                .WithTitle("📣 You've been set as event host")
                .WithDescription(
                    $"{setterName} set you as the host of **{title}**.\n\n" +
                    $"🕒 {EventTimeParser.Stamp(startUtc, 'F')} ({EventTimeParser.Stamp(startUtc, 'R')})")
                .Build();

            await DmGuard.TrySendAsync(host, embed);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to DM new host {Host} for templated event", hostId);
        }
    }

    private async Task Finalize(SocketMessageComponent c, string content)
    {
        try { await c.ModifyOriginalResponseAsync(m => { m.Content = content; m.Embed = null; m.Components = Empty(); m.AllowedMentions = AllowedMentions.None; }); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to finalize template use message"); }
    }

    // ─── /event template edit ─────────────────────────────────────────────

    private async Task StartEditAsync(SocketSlashCommand command, SocketGuildUser user)
    {
        if (!HasManagePermission(user))
        {
            await command.FollowupAsync(
                $"❌ Editing templates is restricted to **{_config.EventTemplateManageMinRank} and above**.", ephemeral: true);
            return;
        }

        List<ClanEventTemplate> templates;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            templates = await db.ClanEventTemplates
                .Where(t => t.GuildId == command.GuildId!.Value)
                .OrderBy(t => t.Name)
                .Take(25)
                .ToListAsync();
        }

        if (templates.Count == 0)
        {
            await command.FollowupAsync("There are no templates to edit yet.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"{Prefix}edit:pick")
            .WithPlaceholder("Choose a template to edit")
            .WithMinValues(1).WithMaxValues(1);

        foreach (var t in templates)
            menu.AddOption(Truncate(t.Name, 100), t.Id.ToString(), Truncate(t.Title, 100));

        await command.FollowupAsync("Which template would you like to edit?",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    private async Task OnEditPickAsync(SocketMessageComponent component)
    {
        if (component.User is not SocketGuildUser gu || !HasManagePermission(gu))
        {
            await component.UpdateAsync(m => { m.Content = "You don't have permission to edit templates."; m.Components = Empty(); });
            return;
        }
        if (!int.TryParse(component.Data.Values.FirstOrDefault(), out var templateId))
        {
            await component.UpdateAsync(m => { m.Content = "Couldn't read that selection."; m.Components = Empty(); });
            return;
        }

        ClanEventTemplate? t;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            t = await db.ClanEventTemplates.FirstOrDefaultAsync(x => x.Id == templateId);
        }
        if (t is null || t.GuildId != gu.Guild.Id)
        {
            await component.UpdateAsync(m => { m.Content = "That template no longer exists."; m.Components = Empty(); });
            return;
        }

        var (dm, dmFailure) = await DmGuard.TryOpenAsync(component.User);
        if (dm is null)
        {
            _logger.LogDebug("Couldn't open DM for template edit ({User}): {Failure}",
                component.User.Id, dmFailure);
            await component.UpdateAsync(m =>
            {
                m.Content    = DmGuard.AdviceFor(dmFailure);
                m.Components = Empty();
            });
            return;
        }

        // Only one DM flow at a time — drop any in-progress save session.
        _saveSessions.TryRemove(component.User.Id, out _);

        var session = new EditSession
        {
            UserId          = component.User.Id,
            Dm              = dm,
            GuildId         = gu.Guild.Id,
            TemplateId      = t.Id,
            Step            = EditField.Menu,
            LastActivityAt  = DateTime.UtcNow,
            Name            = t.Name,
            Title           = t.Title,
            DurationMinutes = t.DurationMinutes,
            Description     = t.Description ?? string.Empty,
            MaxParticipants = t.MaxParticipants,
            HasImage        = !string.IsNullOrWhiteSpace(t.ImageFileName),
        };
        _editSessions[component.User.Id] = session;

        try
        {
            await SendEditMenuAsync(session);
            await component.UpdateAsync(m => { m.Content = "📬 I've sent you a DM to edit this template."; m.Components = Empty(); });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't send template edit menu ({User})", component.User.Id);
            _editSessions.TryRemove(component.User.Id, out _);
            await component.UpdateAsync(m =>
            {
                m.Content = "I couldn't DM you — enable Direct Messages from server members and try again.";
                m.Components = Empty();
            });
        }
    }

    private async Task SendEditMenuAsync(EditSession s)
    {
        s.Step = EditField.Menu;

        var imageState = s.ImageChanged
            ? (s.ImageCleared ? "Will be removed" : "New image staged")
            : (s.HasImage ? "Set" : "None");

        var embed = new EmbedBuilder()
            .WithColor(FormColor)
            .WithTitle("✏️ Edit template")
            .WithDescription("Type a **number** to change that field. Type **done** to save, or **cancel** to discard.")
            .AddField("1 · Template name", FormBox(s.Name))
            .AddField("2 · Event title", FormBox(s.Title))
            .AddField("3 · Default length", FormBox(FormatDuration(s.DurationMinutes)), inline: true)
            .AddField("4 · Attendee limit", FormBox(s.MaxParticipants is int c ? $"{c} max" : "No limit"), inline: true)
            .AddField("5 · Banner image", FormBox(imageState), inline: true)
            .AddField("6 · Description", FormBox(string.IsNullOrWhiteSpace(s.Description) ? "—" : s.Description))
            .WithFooter("Reply in this DM • \"done\" to save • \"cancel\" to discard • times out in 15 min");

        await s.Dm.SendMessageAsync(embed: embed.Build());
    }

    private async Task OnEditDmAsync(SocketMessage message)
    {
        if (message.Author.IsBot) return;
        if (message is not SocketUserMessage) return;
        if (message.Channel is not IDMChannel) return;
        if (!_editSessions.TryGetValue(message.Author.Id, out var s)) return;

        if (IsExpired(s))
        {
            _editSessions.TryRemove(message.Author.Id, out _);
            try { await s.Dm.SendMessageAsync(embed: Form("⌛ Template edit timed out",
                "That edit expired from inactivity — **nothing was changed**. Run `/event template edit` to retry.")); } catch { }
            return;
        }

        s.LastActivityAt = DateTime.UtcNow;
        var text = message.Content.Trim();

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _editSessions.TryRemove(message.Author.Id, out _);
            try { await s.Dm.SendMessageAsync(embed: Form("Cancelled", "No changes were saved.")); } catch { }
            return;
        }

        try
        {
            switch (s.Step)
            {
                case EditField.Menu:            await HandleEditMenuAsync(s, text);            break;
                case EditField.Name:            await HandleEditNameAsync(s, text);            break;
                case EditField.Title:           await HandleEditTitleAsync(s, text);           break;
                case EditField.Duration:        await HandleEditDurationAsync(s, text);        break;
                case EditField.Description:     await HandleEditDescriptionAsync(s, text);     break;
                case EditField.Image:           await HandleEditImageAsync(s, message, text);  break;
                case EditField.MaxParticipants: await HandleEditMaxParticipantsAsync(s, text); break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template edit step {Step} failed for {User}", s.Step, message.Author.Id);
            _editSessions.TryRemove(message.Author.Id, out _);
            try { await s.Dm.SendMessageAsync(embed: Form("Something went wrong", "Run `/event template edit` to start over.")); } catch { }
        }
    }

    private async Task HandleEditMenuAsync(EditSession s, string text)
    {
        if (text.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            await SaveEditAsync(s);
            return;
        }

        switch (text)
        {
            case "1":
                s.Step = EditField.Name;
                await s.Dm.SendMessageAsync(embed: Form("🧩 Template name", "Send the new name, or type `keep` to leave it unchanged."));
                break;
            case "2":
                s.Step = EditField.Title;
                await s.Dm.SendMessageAsync(embed: Form("🎯 Event title", "Send the new title, or type `keep` to leave it unchanged."));
                break;
            case "3":
                s.Step = EditField.Duration;
                await s.Dm.SendMessageAsync(embed: Form("⏱️ Default length", "Send the new length (e.g. `2 hours`, `90 minutes`), or `keep`."));
                break;
            case "4":
                s.Step = EditField.MaxParticipants;
                await s.Dm.SendMessageAsync(embed: Form("👥 Attendee limit", "Send a new max number, `none` for no limit, or `keep`."));
                break;
            case "5":
                s.Step = EditField.Image;
                await s.Dm.SendMessageAsync(embed: Form("🖼️ Banner image",
                    "Drag a new image in or paste a link to replace it, type `remove` to clear it, or `keep` to leave it as-is."));
                break;
            case "6":
                s.Step = EditField.Description;
                await s.Dm.SendMessageAsync(embed: Form("📝 Description", "Send the new description, `none` to clear it, or `keep`."));
                break;
            default:
                await s.Dm.SendMessageAsync(embed: Form("Pick a field", "Type a number from **1–6**, **done** to save, or **cancel**."));
                break;
        }
    }

    private async Task HandleEditNameAsync(EditSession s, string text)
    {
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) { await SendEditMenuAsync(s); return; }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80)
        {
            await s.Dm.SendMessageAsync(embed: Form("🧩 Template name", "Give a name under 80 characters, or type `keep`."));
            return;
        }

        if (!text.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var dupe = await db.ClanEventTemplates
                .Where(t => t.GuildId == s.GuildId && t.Id != s.TemplateId)
                .AnyAsync(t => t.Name.ToLower() == text.ToLower());
            if (dupe)
            {
                await s.Dm.SendMessageAsync(embed: Form("🧩 That name's taken",
                    $"Another template is already called **{text}**. Pick a different name, or type `keep`."));
                return;
            }
        }

        s.Name = text;
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditTitleAsync(EditSession s, string text)
    {
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) { await SendEditMenuAsync(s); return; }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 100)
        {
            await s.Dm.SendMessageAsync(embed: Form("🎯 Event title", "Give a title under 100 characters, or type `keep`."));
            return;
        }
        s.Title = text;
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditDurationAsync(EditSession s, string text)
    {
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) { await SendEditMenuAsync(s); return; }
        var minutes = ParseDurationMinutes(text);
        if (minutes is null or <= 0)
        {
            await s.Dm.SendMessageAsync(embed: Form("⏱️ Need a length", "Try `2 hours`, `90 minutes`, or `1h 30m` — or type `keep`."));
            return;
        }
        s.DurationMinutes = minutes.Value;
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditDescriptionAsync(EditSession s, string text)
    {
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) { await SendEditMenuAsync(s); return; }
        if (text.Length > 4096
         && !text.Equals("none", StringComparison.OrdinalIgnoreCase)
         && !text.Equals("skip", StringComparison.OrdinalIgnoreCase))
        {
            await s.Dm.SendMessageAsync(embed: Form("📝 Description too long",
                "Keep it under 4096 characters — trim it down, or type `none` to clear it."));
            return;
        }
        s.Description = text.Equals("none", StringComparison.OrdinalIgnoreCase)
                     || text.Equals("skip", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : text;
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditMaxParticipantsAsync(EditSession s, string text)
    {
        text = text.Trim();
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) { await SendEditMenuAsync(s); return; }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase)
         || text.Equals("unlimited", StringComparison.OrdinalIgnoreCase)
         || text == "0")
        {
            s.MaxParticipants = null;
        }
        else if (int.TryParse(text, out var n) && n > 0)
        {
            s.MaxParticipants = n;
        }
        else
        {
            await s.Dm.SendMessageAsync(embed: Form("👥 Need a number", "Give a whole number above 0, `none` for no limit, or `keep`."));
            return;
        }
        await SendEditMenuAsync(s);
    }

    private async Task HandleEditImageAsync(EditSession s, SocketMessage message, string text)
    {
        if (text.Equals("keep", StringComparison.OrdinalIgnoreCase)) { await SendEditMenuAsync(s); return; }

        if (text.Equals("remove", StringComparison.OrdinalIgnoreCase)
         || text.Equals("none", StringComparison.OrdinalIgnoreCase)
         || text.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            s.ImageChanged = true;
            s.ImageCleared = true;
            s.ImageBytes = null;
            s.ImageFileName = null;
            await SendEditMenuAsync(s);
            return;
        }

        var att = message.Attachments.FirstOrDefault();
        if (att is null)
        {
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
             || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await s.Dm.SendMessageAsync("⏳ Fetching that image…");
                var res = await EventImageFetcher.FromUrlAsync(text);
                if (!res.Ok)
                {
                    await s.Dm.SendMessageAsync(embed: Form("🖼️ Couldn't use that link",
                        $"{res.Error} Try another link, drag the file in, type `remove`, or `keep`."));
                    return;
                }
                s.ImageChanged = true;
                s.ImageCleared = false;
                s.ImageFileName = res.FileName;
                s.ImageBytes    = EventImage.Downscale(res.Bytes!, res.FileName);
                await SendEditMenuAsync(s);
                return;
            }

            await s.Dm.SendMessageAsync(embed: Form("🖼️ Banner image",
                "Drag an image in, paste a link, type `remove` to clear it, or `keep`."));
            return;
        }

        var looksImage = (att.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false)
                      || EventImage.IsAllowedExtension(att.Filename);
        if (!looksImage)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Not an image", "That doesn't look like a PNG/JPG/GIF/WebP. Try another, or type `keep`."));
            return;
        }
        if (att.Size > EventImage.MaxBytes)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image too large",
                $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB — try a smaller one, or type `keep`."));
            return;
        }

        byte[] bytes;
        try { bytes = await Http.GetByteArrayAsync(att.Url); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download template edit image for {User}", s.UserId);
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Download failed", "Couldn't download that image. Try again, or type `keep`."));
            return;
        }
        if (bytes.Length > EventImage.MaxBytes)
        {
            await s.Dm.SendMessageAsync(embed: Form("🖼️ Image too large",
                $"Max is {EventImage.MaxBytes / (1024 * 1024)} MB — try a smaller one, or type `keep`."));
            return;
        }

        var imgName = EventImage.Sanitize(att.Filename);
        s.ImageChanged = true;
        s.ImageCleared = false;
        s.ImageFileName = imgName;
        s.ImageBytes    = EventImage.Downscale(bytes, imgName);
        await SendEditMenuAsync(s);
    }

    private async Task SaveEditAsync(EditSession s)
    {
        _editSessions.TryRemove(s.UserId, out _);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var t = await db.ClanEventTemplates.FirstOrDefaultAsync(x => x.Id == s.TemplateId);
        if (t is null || t.GuildId != s.GuildId)
        {
            try { await s.Dm.SendMessageAsync(embed: Form("Gone", "That template no longer exists — nothing was saved.")); } catch { }
            return;
        }

        // Re-check name uniqueness at save time (another officer may have taken it).
        if (!t.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
        {
            var dupe = await db.ClanEventTemplates
                .Where(x => x.GuildId == s.GuildId && x.Id != s.TemplateId)
                .AnyAsync(x => x.Name.ToLower() == s.Name.ToLower());
            if (dupe)
            {
                try { await s.Dm.SendMessageAsync(embed: Form("🧩 That name's taken",
                    $"Another template is now called **{s.Name}** — nothing was saved. Run `/event template edit` again.")); } catch { }
                return;
            }
        }

        t.Name            = s.Name;
        t.Title           = s.Title;
        t.DurationMinutes = s.DurationMinutes;
        t.Description     = s.Description;
        t.MaxParticipants = s.MaxParticipants;
        if (s.ImageChanged)
        {
            t.ImageBytes    = s.ImageCleared ? null : s.ImageBytes;
            t.ImageFileName = s.ImageCleared ? null : s.ImageFileName;
        }
        await db.SaveChangesAsync();

        try
        {
            await s.Dm.SendMessageAsync(embed: Form("✅ Template saved",
                $"Updated **{t.Name}**. Events made from it going forward will use the new details — events already created are unchanged."));
        }
        catch { }
    }

    // ─── Permission gates (mirror EventCommandHandler.HasEventPermission) ─────

    private bool HasUsePermission(SocketGuildUser user)   => HasRankAtLeast(user, _config.EventCommandMinRank);
    private bool HasManagePermission(SocketGuildUser user) => HasRankAtLeast(user, _config.EventTemplateManageMinRank);

    private bool HasRankAtLeast(SocketGuildUser user, string minRank)
    {
        if (user.GuildPermissions.Administrator || user.GuildPermissions.ManageRoles)
            return true;

        var rankRoles = _config.GetRankRolesList();
        var minIndex  = rankRoles.IndexOf(minRank);
        if (minIndex < 0)
        {
            _logger.LogWarning("Rank gate '{MinRank}' not found in RankRoles — template action will be admin-only", minRank);
            return false;
        }

        var highest = user.Roles.Select(r => rankRoles.IndexOf(r.Name)).DefaultIfEmpty(-1).Max();
        return highest >= minIndex;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string> ResolveZoneIdAsync(ulong userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var iana = (await db.UserTimeZones.FirstOrDefaultAsync(t => t.UserId == userId))?.IanaId;
        return string.IsNullOrWhiteSpace(iana) ? _config.EventDefaultTimeZone : iana;
    }

    /// <summary>Parses "2 hours", "90 minutes", "1h 30m", "45", etc. into minutes.</summary>
    private static int? ParseDurationMinutes(string text)
    {
        text = text.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Bare number → minutes.
        if (int.TryParse(text, out var bare) && bare > 0) return bare;

        double minutes = 0;
        var matched = false;

        var hMatch = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hour|hours)");
        if (hMatch.Success && double.TryParse(hMatch.Groups[1].Value, out var h)) { minutes += h * 60; matched = true; }

        var mMatch = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s*(?:m|min|mins|minute|minutes)");
        if (mMatch.Success && int.TryParse(mMatch.Groups[1].Value, out var m)) { minutes += m; matched = true; }

        if (!matched) return null;
        return (int)Math.Round(minutes);
    }

    private static string FormatDuration(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? $"{h}h" : $"{h}h {m}m";
    }

    /// <summary>Apollo-style grey value box (a code fence renders monospace and boxed).</summary>
    private static string FormBox(string value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "—" : value;
        if (v.Length > 1000) v = v[..1000] + "…";
        return $"```\n{v}\n```";
    }

    private static Embed Form(string title, string? body = null)
    {
        var eb = new EmbedBuilder()
            .WithColor(FormColor)
            .WithTitle(title)
            .WithFooter("Reply in this DM • type \"cancel\" to quit • times out after 15 min");
        if (!string.IsNullOrWhiteSpace(body)) eb.WithDescription(body);
        return eb.Build();
    }

    private static MessageComponent Empty() => new ComponentBuilder().Build();

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private static bool IsExpired(SaveSession s) => DateTime.UtcNow - s.LastActivityAt > IdleTimeout;
    private static bool IsExpired(EditSession s) => DateTime.UtcNow - s.LastActivityAt > IdleTimeout;

    private async Task SafeSend(SaveSession s, Embed embed)
    {
        try { await s.Dm.SendMessageAsync(embed: embed); }
        catch (Exception ex) { _logger.LogDebug(ex, "Failed to send template DM to {User}", s.UserId); }
    }

    private async Task SweepIdleAsync()
    {
        try
        {
            foreach (var kv in _saveSessions)
            {
                if (!IsExpired(kv.Value)) continue;
                if (_saveSessions.TryRemove(kv.Key, out var s))
                {
                    try
                    {
                        await s.Dm.SendMessageAsync(embed: Form("⌛ Template setup timed out",
                            "I didn't hear back, so I've cancelled this setup and **nothing was saved**. Run `/event template save` when you're ready."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send template timeout DM to {User}", kv.Key); }
                }
            }

            foreach (var kv in _editSessions)
            {
                if (!IsExpired(kv.Value)) continue;
                if (_editSessions.TryRemove(kv.Key, out var s))
                {
                    try
                    {
                        await s.Dm.SendMessageAsync(embed: Form("⌛ Template edit timed out",
                            "I didn't hear back, so I've cancelled this edit and **nothing was changed**. Run `/event template edit` when you're ready."));
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Failed to send template edit timeout DM to {User}", kv.Key); }
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Template idle sweep failed"); }
    }
}
