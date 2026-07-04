using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Owns the entire runtime flow of the ticket system:
///
///   • Category select ("ticket:cat")          → creation modal
///   • Creation modal ("ticket:create:{key}")  → private thread + route + ping
///   • Claim button ("ticket:claim:{id}")      → assign handler, flip to InProgress
///   • Priority button ("ticket:priomenu:{id}")→ ephemeral priority select
///   • Priority select ("ticket:prio:{id}")    → update priority + embed
///   • Close button ("ticket:closebtn:{id}")   → close-reason modal
///   • Close modal ("ticket:close:{id}")       → transcript → log channel → lock
///   • MessageReceived (in a ticket thread)    → capture for transcript + activity
///
/// ── Transports ──
/// Identified tickets: a PRIVATE thread under the tickets channel, with the
/// opener + the routed role's members added. Anonymous tickets (categories
/// flagged anonymousAllowed): the opener is NOT added and the embed shows an
/// anonymized handle; the report is delivered to staff without revealing who
/// filed it. (Two-way anonymous relay is a later phase — the initial report
/// captured in the modal is delivered in full now.)
///
/// ── Permissions ──
/// Claim / priority / close controls require ticket staff: Administrator, the
/// configured TicketHqRoleId, or the ticket's routed role. The opener may also
/// close their own (identified) ticket.
/// </summary>
public sealed class TicketInteractionHandler
{
    // ── CustomId scheme: "ticket:{action}[:{arg}]" ──
    public const string CategorySelectId = "ticket:cat";
    private const string ActionCreate    = "create";   // modal:  ticket:create:{categoryKey}
    private const string ActionClaim     = "claim";    // button: ticket:claim:{id}
    private const string ActionPrioMenu  = "priomenu"; // button: ticket:priomenu:{id}
    private const string ActionPrio      = "prio";     // select: ticket:prio:{id}
    private const string ActionCloseBtn  = "closebtn"; // button: ticket:closebtn:{id}
    private const string ActionClose     = "close";    // modal:  ticket:close:{id}

    private const string ModalSubjectId  = "subject";
    private const string ModalDetailsId  = "details";
    private const string ModalReasonId   = "reason";

    private const int MaxOpenTicketsPerOpener = 3;
    private const int MaxThreadMembersToAdd   = 50;

    private readonly IServiceProvider _services;
    private readonly ILogger<TicketInteractionHandler> _logger;
    private readonly BotConfig _config;

    public TicketInteractionHandler(
        IServiceProvider services,
        ILogger<TicketInteractionHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _logger   = logger;
        _config   = config.Value;
    }

    public void Register(DiscordSocketClient client)
    {
        client.SelectMenuExecuted += OnSelectMenuAsync;
        client.ButtonExecuted     += OnButtonAsync;
        client.ModalSubmitted     += OnModalAsync;
        client.MessageReceived    += OnMessageReceivedAsync;
    }

    // ─── Static component builders (shared with the panel command) ─────

    /// <summary>Builds the category select menu shown on the ticket panel.</summary>
    public static MessageComponent BuildPanelComponents(IReadOnlyList<TicketCategoryDef> categories)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(CategorySelectId)
            .WithPlaceholder("Open a ticket…")
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var c in categories.Take(25))
        {
            var opt = new SelectMenuOptionBuilder()
                .WithLabel(Truncate(c.Label, 100))
                .WithValue(c.Key);

            if (!string.IsNullOrWhiteSpace(c.Emoji))
            {
                try { opt.WithEmote(new Emoji(c.Emoji)); } catch { /* bad emoji — skip */ }
            }
            if (c.AnonymousAllowed)
                opt.WithDescription("Can be submitted anonymously");

            menu.AddOption(opt);
        }

        return new ComponentBuilder().WithSelectMenu(menu).Build();
    }

    private static MessageComponent BuildControlButtons(int ticketId) =>
        new ComponentBuilder()
            .WithButton("Claim",        $"ticket:{ActionClaim}:{ticketId}",    ButtonStyle.Primary,   new Emoji("🙋"))
            .WithButton("Set Priority", $"ticket:{ActionPrioMenu}:{ticketId}", ButtonStyle.Secondary, new Emoji("⚙️"))
            .WithButton("Close",        $"ticket:{ActionCloseBtn}:{ticketId}", ButtonStyle.Danger,    new Emoji("🔒"))
            .Build();

    // ─── Category select → creation modal ─────────────────────────────

    private async Task OnSelectMenuAsync(SocketMessageComponent component)
    {
        try
        {
            if (component.Data.CustomId == CategorySelectId)
            {
                await HandleCategorySelectAsync(component);
            }
            else if (TryParse(component.Data.CustomId, ActionPrio, out var ticketId))
            {
                await HandlePrioritySelectAsync(component, ticketId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling ticket select menu {CustomId}", component.Data.CustomId);
            await TryRespondErrorAsync(component);
        }
    }

    private async Task HandleCategorySelectAsync(SocketMessageComponent component)
    {
        var member = component.User as SocketGuildUser;
        if (member is null)
        {
            await component.RespondAsync("This only works inside the server.", ephemeral: true);
            return;
        }

        var key = component.Data.Values.FirstOrDefault();
        var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == key);
        if (category is null)
        {
            await component.RespondAsync("That category is no longer available.", ephemeral: true);
            return;
        }

        // Spam guard — cap concurrent open tickets per opener.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var openCount = await db.SupportTickets.CountAsync(t =>
                t.GuildId == member.Guild.Id &&
                t.OpenerUserId == member.Id &&
                t.Status != SupportTicketStatus.Closed);

            if (openCount >= MaxOpenTicketsPerOpener)
            {
                await component.RespondAsync(
                    $"⛔ You already have {openCount} open tickets. Please wait for one to be resolved before opening another.",
                    ephemeral: true);
                return;
            }
        }

        var anonNote = category.AnonymousAllowed
            ? "This category is anonymous — HQ won't see who submitted it."
            : $"Category: {category.Label}";

        var modal = new ModalBuilder()
            .WithTitle(Truncate($"New Ticket — {category.Label}", 45))
            .WithCustomId($"ticket:{ActionCreate}:{category.Key}")
            .AddTextInput(
                label: "Subject",
                customId: ModalSubjectId,
                style: TextInputStyle.Short,
                placeholder: "A short summary of what you need",
                minLength: 3, maxLength: 100, required: true)
            .AddTextInput(
                label: "Details",
                customId: ModalDetailsId,
                style: TextInputStyle.Paragraph,
                placeholder: anonNote,
                minLength: 10, maxLength: 1500, required: true)
            .Build();

        await component.RespondWithModalAsync(modal);
    }

    // ─── Creation modal → thread + route ──────────────────────────────

    private async Task OnModalAsync(SocketModal modal)
    {
        try
        {
            var parts = modal.Data.CustomId.Split(':');
            if (parts.Length < 2 || parts[0] != "ticket") return;

            if (parts[1] == ActionCreate && parts.Length == 3)
                await HandleCreateAsync(modal, parts[2]);
            else if (parts[1] == ActionClose && parts.Length == 3 && int.TryParse(parts[2], out var id))
                await HandleCloseSubmitAsync(modal, id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling ticket modal {CustomId}", modal.Data.CustomId);
            try
            {
                if (modal.HasResponded)
                    await modal.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
                else
                    await modal.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            }
            catch { /* swallow */ }
        }
    }

    private async Task HandleCreateAsync(SocketModal modal, string categoryKey)
    {
        await modal.DeferAsync(ephemeral: true);

        var member = modal.User as SocketGuildUser;
        if (member is null)
        {
            await modal.FollowupAsync("This only works inside the server.", ephemeral: true);
            return;
        }
        var guild = member.Guild;

        var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == categoryKey);
        if (category is null)
        {
            await modal.FollowupAsync("That category is no longer available.", ephemeral: true);
            return;
        }

        var centerChannel = _config.TicketCenterChannelId == 0
            ? null
            : guild.GetTextChannel(_config.TicketCenterChannelId);
        if (centerChannel is null)
        {
            await modal.FollowupAsync("⚠️ The ticket system isn't configured correctly. Please ping an officer.", ephemeral: true);
            return;
        }

        var fields  = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "");
        var subject = fields.GetValueOrDefault(ModalSubjectId, "").Trim();
        var details = fields.GetValueOrDefault(ModalDetailsId, "").Trim();

        var routedRoleId = category.RoutedRoleId != 0 ? category.RoutedRoleId : _config.TicketHqRoleId;
        var now = DateTime.UtcNow;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // Persist first so we have the ticket Id for naming + custom IDs.
        var ticket = new SupportTicket
        {
            GuildId           = guild.Id,
            CategoryKey       = category.Key,
            CategoryLabel     = category.Label,
            OpenerUserId      = member.Id,
            OpenerDisplayName = member.DisplayName ?? member.Username,
            Subject           = subject,
            Details           = details,
            IsAnonymous       = category.AnonymousAllowed,
            RoutedRoleId      = routedRoleId,
            Status            = SupportTicketStatus.Open,
            Priority          = category.DefaultPriority,
            CreatedUtc        = now,
            LastActivityUtc   = now,
        };
        if (ticket.IsAnonymous)
            ticket.AnonHandle = $"Anonymous #{Guid.NewGuid().ToString("N")[..4]}";

        db.SupportTickets.Add(ticket);
        await db.SaveChangesAsync();

        // Create the private thread.
        var threadName = BuildThreadName(ticket, category);
        SocketThreadChannel thread;
        try
        {
            thread = await centerChannel.CreateThreadAsync(
                threadName,
                type: ThreadType.PrivateThread,
                autoArchiveDuration: ThreadArchiveDuration.OneWeek);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create ticket thread for ticket #{Id}", ticket.Id);
            await modal.FollowupAsync(
                "⚠️ Couldn't create your ticket thread — the bot may be missing the "
                + "\"Create Private Threads\" permission in the tickets channel. Please ping an officer.",
                ephemeral: true);
            return;
        }

        // Add the opener (identified only) + the routed role's members.
        if (!ticket.IsAnonymous)
        {
            try { await thread.AddUserAsync(member); } catch (Exception ex)
            { _logger.LogWarning(ex, "Could not add opener to ticket thread #{Id}", ticket.Id); }
        }
        await AddRoleMembersToThreadAsync(thread, guild, routedRoleId);

        // Post the ticket embed with control buttons.
        var embed = BuildTicketEmbed(ticket, category, guild);
        var ping  = routedRoleId != 0 ? $"<@&{routedRoleId}> — new ticket" : "New ticket";
        var control = await thread.SendMessageAsync(
            text: ping,
            embed: embed,
            components: BuildControlButtons(ticket.Id),
            allowedMentions: routedRoleId != 0
                ? new AllowedMentions { RoleIds = new List<ulong> { routedRoleId }, MentionRepliedUser = false }
                : AllowedMentions.None);

        ticket.ThreadId         = thread.Id;
        ticket.ControlMessageId = control.Id;

        db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            TicketId          = ticket.Id,
            AuthorUserId      = member.Id,
            AuthorDisplayName = ticket.IsAnonymous ? (ticket.AnonHandle ?? "Anonymous") : ticket.OpenerDisplayName,
            Content           = $"Opened ticket. Subject: {subject}",
            SentUtc           = now,
            Direction         = SupportTicketMessageDirection.System,
        });
        await db.SaveChangesAsync();

        _logger.LogInformation(
            "Ticket #{Id} ({Category}, anon={Anon}) opened by {User} → thread {ThreadId}",
            ticket.Id, category.Key, ticket.IsAnonymous, member.Username, thread.Id);

        if (ticket.IsAnonymous)
        {
            await modal.FollowupAsync(
                "✅ Your **anonymous** report has been submitted to HQ. Because it's anonymous "
                + "you won't see the ticket thread — HQ will handle it without seeing who filed it.",
                ephemeral: true);
        }
        else
        {
            var link = $"https://discord.com/channels/{guild.Id}/{thread.Id}";
            await modal.FollowupAsync($"✅ Ticket **#{ticket.Id}** opened: {link}", ephemeral: true);
        }
    }

    // ─── Claim ────────────────────────────────────────────────────────

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        try
        {
            if (TryParse(component.Data.CustomId, ActionClaim, out var claimId))
                await HandleClaimAsync(component, claimId);
            else if (TryParse(component.Data.CustomId, ActionPrioMenu, out var pmId))
                await HandlePriorityMenuAsync(component, pmId);
            else if (TryParse(component.Data.CustomId, ActionCloseBtn, out var closeId))
                await HandleCloseButtonAsync(component, closeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling ticket button {CustomId}", component.Data.CustomId);
            await TryRespondErrorAsync(component);
        }
    }

    private async Task HandleClaimAsync(SocketMessageComponent component, int ticketId)
    {
        await component.DeferAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return;

        var user = component.User as SocketGuildUser;
        if (user is null || !HasStaffPermission(user, ticket))
        {
            await component.FollowupAsync("⛔ Only the assigned team can claim this ticket.", ephemeral: true);
            return;
        }
        if (ticket.Status == SupportTicketStatus.Closed)
        {
            await component.FollowupAsync("This ticket is already closed.", ephemeral: true);
            return;
        }
        if (ticket.ClaimedByUserId is { } existing)
        {
            await component.FollowupAsync(
                existing == user.Id ? "You've already claimed this ticket." : $"Already claimed by <@{existing}>.",
                ephemeral: true);
            return;
        }

        var now = DateTime.UtcNow;
        ticket.ClaimedByUserId   = user.Id;
        ticket.ClaimedByUsername = user.DisplayName ?? user.Username;
        ticket.ClaimedAtUtc      = now;
        ticket.Status            = SupportTicketStatus.InProgress;
        ticket.LastActivityUtc   = now;
        db.SupportTicketMessages.Add(SystemMessage(ticket, user, $"Claimed by {ticket.ClaimedByUsername}", now));
        await db.SaveChangesAsync();

        await UpdateControlEmbedAsync(component, ticket);
        await component.Channel.SendMessageAsync(
            $"🙋 {user.Mention} claimed this ticket.", allowedMentions: AllowedMentions.None);
    }

    // ─── Priority ─────────────────────────────────────────────────────

    private async Task HandlePriorityMenuAsync(SocketMessageComponent component, int ticketId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) { await component.RespondAsync("Ticket not found.", ephemeral: true); return; }

        var user = component.User as SocketGuildUser;
        if (user is null || !HasStaffPermission(user, ticket))
        {
            await component.RespondAsync("⛔ Only the assigned team can change priority.", ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId($"ticket:{ActionPrio}:{ticketId}")
            .WithPlaceholder("Select a priority…")
            .WithMinValues(1).WithMaxValues(1);
        foreach (var p in new[] { SupportTicketPriority.Low, SupportTicketPriority.Normal, SupportTicketPriority.High, SupportTicketPriority.Urgent })
            menu.AddOption(p.ToString(), p.ToString(), isDefault: p == ticket.Priority);

        await component.RespondAsync(
            "Set the priority for this ticket:",
            components: new ComponentBuilder().WithSelectMenu(menu).Build(),
            ephemeral: true);
    }

    private async Task HandlePrioritySelectAsync(SocketMessageComponent component, int ticketId)
    {
        await component.DeferAsync();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) return;

        var user = component.User as SocketGuildUser;
        if (user is null || !HasStaffPermission(user, ticket)) return;

        if (!Enum.TryParse<SupportTicketPriority>(component.Data.Values.FirstOrDefault(), out var newPriority))
            return;

        var now = DateTime.UtcNow;
        ticket.Priority        = newPriority;
        ticket.LastActivityUtc = now;
        db.SupportTicketMessages.Add(SystemMessage(ticket, user, $"Priority set to {newPriority}", now));
        await db.SaveChangesAsync();

        await UpdateControlEmbedAsync(component, ticket);
        await component.ModifyOriginalResponseAsync(p =>
        {
            p.Content    = $"✅ Priority set to **{newPriority}**.";
            p.Components = new ComponentBuilder().Build();
        });
    }

    // ─── Close ────────────────────────────────────────────────────────

    private async Task HandleCloseButtonAsync(SocketMessageComponent component, int ticketId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) { await component.RespondAsync("Ticket not found.", ephemeral: true); return; }

        var user = component.User as SocketGuildUser;
        // Opener may close their own identified ticket; otherwise staff only.
        var isOpener = user is not null && !ticket.IsAnonymous && user.Id == ticket.OpenerUserId;
        if (user is null || (!isOpener && !HasStaffPermission(user, ticket)))
        {
            await component.RespondAsync("⛔ Only the assigned team (or the opener) can close this ticket.", ephemeral: true);
            return;
        }
        if (ticket.Status == SupportTicketStatus.Closed)
        {
            await component.RespondAsync("This ticket is already closed.", ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle(Truncate($"Close Ticket #{ticket.Id}", 45))
            .WithCustomId($"ticket:{ActionClose}:{ticketId}")
            .AddTextInput(
                label: "Resolution (optional)",
                customId: ModalReasonId,
                style: TextInputStyle.Paragraph,
                placeholder: "Short note on how this was resolved — included in the transcript.",
                minLength: 0, maxLength: 500, required: false)
            .Build();

        await component.RespondWithModalAsync(modal);
    }

    private async Task HandleCloseSubmitAsync(SocketModal modal, int ticketId)
    {
        await modal.DeferAsync(ephemeral: true);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) { await modal.FollowupAsync("Ticket not found.", ephemeral: true); return; }
        if (ticket.Status == SupportTicketStatus.Closed)
        {
            await modal.FollowupAsync("This ticket is already closed.", ephemeral: true);
            return;
        }

        var guild = (modal.Channel as SocketGuildChannel)?.Guild
                    ?? (modal.User as SocketGuildUser)?.Guild;
        var user = modal.User as SocketGuildUser;
        if (guild is null || user is null)
        {
            await modal.FollowupAsync("Could not resolve the server.", ephemeral: true);
            return;
        }

        var reason = modal.Data.Components.FirstOrDefault(c => c.CustomId == ModalReasonId)?.Value?.Trim();
        var now = DateTime.UtcNow;

        ticket.Status           = SupportTicketStatus.Closed;
        ticket.ClosedUtc        = now;
        ticket.ClosedByUserId   = user.Id;
        ticket.ClosedByUsername = user.DisplayName ?? user.Username;
        ticket.Resolution       = string.IsNullOrWhiteSpace(reason) ? null : reason;
        ticket.LastActivityUtc  = now;
        db.SupportTicketMessages.Add(SystemMessage(ticket, user, $"Closed by {ticket.ClosedByUsername}", now));
        await db.SaveChangesAsync();

        var messages = await db.SupportTicketMessages
            .Where(m => m.TicketId == ticket.Id)
            .OrderBy(m => m.SentUtc)
            .ToListAsync();

        // Post the transcript to the HQ log channel (best-effort).
        await PostTranscriptAsync(guild, ticket, messages);

        // Flip the control embed to closed + drop the buttons.
        var thread = (modal.Channel as SocketThreadChannel)
                     ?? guild.GetChannel(ticket.ThreadId) as SocketThreadChannel;
        if (thread is not null)
        {
            try
            {
                if (await thread.GetMessageAsync(ticket.ControlMessageId) is IUserMessage control)
                {
                    var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == ticket.CategoryKey);
                    await control.ModifyAsync(p =>
                    {
                        p.Embed      = BuildTicketEmbed(ticket, category, guild);
                        p.Components = new ComponentBuilder().Build();
                    });
                }

                await thread.SendMessageAsync(
                    $"🔒 Ticket closed by {user.Mention}."
                    + (ticket.Resolution is { } r ? $"\n**Resolution:** {r}" : ""),
                    allowedMentions: AllowedMentions.None);

                await thread.ModifyAsync(p => { p.Archived = true; p.Locked = true; });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not finalize thread for closed ticket #{Id}", ticket.Id);
            }
        }

        await modal.FollowupAsync($"✅ Ticket **#{ticket.Id}** closed. Transcript posted to the log channel.", ephemeral: true);
        _logger.LogInformation("Ticket #{Id} closed by {User}", ticket.Id, user.Username);
    }

    private async Task PostTranscriptAsync(SocketGuild guild, SupportTicket ticket, List<SupportTicketMessage> messages)
    {
        if (_config.TicketLogChannelId == 0) return;
        var logChannel = guild.GetTextChannel(_config.TicketLogChannelId);
        if (logChannel is null) return;

        var transcript = BuildTranscript(ticket, messages);
        var summary = new EmbedBuilder()
            .WithTitle($"🎫 Ticket #{ticket.Id} closed — {ticket.CategoryLabel}")
            .WithColor(new Color(0x99, 0xAA, 0xB5))
            .AddField("Opener",
                ticket.IsAnonymous ? $"🕵️ {ticket.AnonHandle} — <@{ticket.OpenerUserId}> *(unmasked, HQ-only)*"
                                   : $"<@{ticket.OpenerUserId}>", inline: true)
            .AddField("Priority", ticket.Priority.ToString(), inline: true)
            .AddField("Closed by", ticket.ClosedByUsername ?? "—", inline: true)
            .AddField("Subject", Truncate(ticket.Subject, 1024), inline: false)
            .WithFooter($"Opened {ticket.CreatedUtc:yyyy-MM-dd HH:mm} UTC • Closed {ticket.ClosedUtc:yyyy-MM-dd HH:mm} UTC")
            .Build();

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcript));
            await logChannel.SendFileAsync(
                stream, $"ticket-{ticket.Id}-transcript.txt",
                embed: summary, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post transcript for ticket #{Id}", ticket.Id);
        }
    }

    // ─── Message capture (transcript + activity) ──────────────────────

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        try
        {
            if (message.Author.IsBot) return;
            if (message is not SocketUserMessage) return;
            if (message.Channel is not SocketThreadChannel thread) return;
            if (thread.ParentChannel?.Id != _config.TicketCenterChannelId) return;
            if (string.IsNullOrWhiteSpace(message.Content)) return;

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var ticket = await db.SupportTickets
                .FirstOrDefaultAsync(t => t.ThreadId == thread.Id && t.Status != SupportTicketStatus.Closed);
            if (ticket is null) return;

            var author = message.Author as SocketGuildUser;
            var direction = message.Author.Id == ticket.OpenerUserId
                ? SupportTicketMessageDirection.FromOpener
                : SupportTicketMessageDirection.FromStaff;

            db.SupportTicketMessages.Add(new SupportTicketMessage
            {
                TicketId          = ticket.Id,
                AuthorUserId      = message.Author.Id,
                AuthorDisplayName = author?.DisplayName ?? message.Author.Username,
                Content           = Truncate(message.Content, 4000),
                SentUtc           = message.Timestamp.UtcDateTime,
                Direction         = direction,
            });
            ticket.LastActivityUtc = message.Timestamp.UtcDateTime;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture ticket thread message");
        }
    }

    // ─── Embeds / transcript ──────────────────────────────────────────

    private Embed BuildTicketEmbed(SupportTicket ticket, TicketCategoryDef? category, SocketGuild guild)
    {
        var emoji = category?.Emoji;
        var titlePrefix = string.IsNullOrWhiteSpace(emoji) ? "🎫" : emoji;

        var openerValue = ticket.IsAnonymous
            ? $"🕵️ {ticket.AnonHandle}"
            : $"<@{ticket.OpenerUserId}>";

        var statusLabel = ticket.Status switch
        {
            SupportTicketStatus.Open       => "🟢 Open",
            SupportTicketStatus.InProgress => "🔵 In progress",
            SupportTicketStatus.Waiting    => "🟡 Waiting",
            SupportTicketStatus.Closed     => "⚪ Closed",
            _                              => ticket.Status.ToString(),
        };

        var builder = new EmbedBuilder()
            .WithTitle(Truncate($"{titlePrefix} Ticket #{ticket.Id} — {ticket.CategoryLabel}", 256))
            .WithColor(ticket.Status == SupportTicketStatus.Closed ? new Color(0x99, 0xAA, 0xB5) : PriorityColor(ticket.Priority))
            .WithDescription(Truncate($"**{ticket.Subject}**\n\n{ticket.Details}", 4000))
            .AddField("Opened by", openerValue, inline: true)
            .AddField("Priority", ticket.Priority.ToString(), inline: true)
            .AddField("Status", statusLabel, inline: true)
            .WithFooter($"Opened {ticket.CreatedUtc:yyyy-MM-dd HH:mm} UTC");

        if (ticket.ClaimedByUsername is { } claimant && ticket.Status != SupportTicketStatus.Closed)
            builder.AddField("Claimed by", claimant, inline: true);

        if (!string.IsNullOrWhiteSpace(_config.TicketPanelThumbnailUrl))
            builder.WithThumbnailUrl(_config.TicketPanelThumbnailUrl);

        return builder.Build();
    }

    private static string BuildTranscript(SupportTicket ticket, List<SupportTicketMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"189th Support Ticket #{ticket.Id} — {ticket.CategoryLabel}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Status:     Closed");
        sb.AppendLine($"Opener:     {ticket.OpenerDisplayName} ({ticket.OpenerUserId})"
                      + (ticket.IsAnonymous ? $"  [ANONYMOUS as {ticket.AnonHandle}]" : ""));
        sb.AppendLine($"Priority:   {ticket.Priority}");
        sb.AppendLine($"Opened:     {ticket.CreatedUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"Closed:     {ticket.ClosedUtc:yyyy-MM-dd HH:mm} UTC by {ticket.ClosedByUsername}");
        if (ticket.ClaimedByUsername is { } claimant)
            sb.AppendLine($"Claimed by: {claimant}");
        if (ticket.Resolution is { } r)
            sb.AppendLine($"Resolution: {r}");
        sb.AppendLine();
        sb.AppendLine("--- Original submission ---");
        sb.AppendLine($"Subject: {ticket.Subject}");
        sb.AppendLine(ticket.Details);
        sb.AppendLine();
        sb.AppendLine("--- Conversation ---");
        foreach (var m in messages)
        {
            var who = m.Direction switch
            {
                SupportTicketMessageDirection.FromOpener => "opener",
                SupportTicketMessageDirection.FromStaff  => "staff",
                _                                        => "system",
            };
            sb.AppendLine($"[{m.SentUtc:yyyy-MM-dd HH:mm}] {m.AuthorDisplayName} ({who}): {m.Content}");
        }
        return sb.ToString();
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private async Task AddRoleMembersToThreadAsync(SocketThreadChannel thread, SocketGuild guild, ulong roleId)
    {
        if (roleId == 0) return;
        var role = guild.GetRole(roleId);
        if (role is null) return;

        var added = 0;
        foreach (var m in role.Members)
        {
            if (added >= MaxThreadMembersToAdd) break;
            try { await thread.AddUserAsync(m); added++; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not add {User} to ticket thread", m.Id); }
        }
    }

    private async Task UpdateControlEmbedAsync(SocketMessageComponent component, SupportTicket ticket)
    {
        var guild = (component.Channel as SocketGuildChannel)?.Guild
                    ?? (component.User as SocketGuildUser)?.Guild;
        if (guild is null) return;

        var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == ticket.CategoryKey);
        try
        {
            if (component.Channel is SocketThreadChannel thread &&
                await thread.GetMessageAsync(ticket.ControlMessageId) is IUserMessage control)
            {
                await control.ModifyAsync(p => p.Embed = BuildTicketEmbed(ticket, category, guild));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update control embed for ticket #{Id}", ticket.Id);
        }
    }

    private bool HasStaffPermission(SocketGuildUser user, SupportTicket ticket)
    {
        if (user.GuildPermissions.Administrator) return true;
        if (_config.TicketHqRoleId != 0 && user.Roles.Any(r => r.Id == _config.TicketHqRoleId)) return true;
        if (ticket.RoutedRoleId != 0 && user.Roles.Any(r => r.Id == ticket.RoutedRoleId)) return true;
        return false;
    }

    private static SupportTicketMessage SystemMessage(SupportTicket ticket, SocketGuildUser actor, string content, DateTime whenUtc) =>
        new()
        {
            TicketId          = ticket.Id,
            AuthorUserId      = actor.Id,
            AuthorDisplayName = actor.DisplayName ?? actor.Username,
            Content           = content,
            SentUtc           = whenUtc,
            Direction         = SupportTicketMessageDirection.System,
        };

    private static Color PriorityColor(SupportTicketPriority p) => p switch
    {
        SupportTicketPriority.Low    => new Color(0x99, 0xAA, 0xB5),
        SupportTicketPriority.Normal => new Color(0x58, 0x65, 0xF2),
        SupportTicketPriority.High   => new Color(0xE6, 0x7E, 0x22),
        SupportTicketPriority.Urgent => new Color(0xE7, 0x4C, 0x3C),
        _                            => new Color(0x58, 0x65, 0xF2),
    };

    private static string BuildThreadName(SupportTicket ticket, TicketCategoryDef category)
    {
        var prefix = $"#{ticket.Id} {category.Label} — ";
        var subject = string.IsNullOrWhiteSpace(ticket.Subject) ? "ticket" : ticket.Subject;
        const int maxLen = 100;
        var remaining = maxLen - prefix.Length;
        if (remaining < 5) return Truncate(prefix, maxLen);
        if (subject.Length > remaining) subject = subject[..(remaining - 1)] + "…";
        return prefix + subject;
    }

    private static bool TryParse(string customId, string expectedAction, out int id)
    {
        id = 0;
        var parts = customId.Split(':');
        if (parts.Length != 3 || parts[0] != "ticket" || parts[1] != expectedAction) return false;
        return int.TryParse(parts[2], out id);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    private static async Task TryRespondErrorAsync(SocketMessageComponent component)
    {
        try
        {
            if (component.HasResponded)
                await component.FollowupAsync("Something went wrong. Check the bot logs.", ephemeral: true);
            else
                await component.RespondAsync("Something went wrong. Check the bot logs.", ephemeral: true);
        }
        catch { /* swallow */ }
    }
}
