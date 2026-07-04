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
    private const string ActionRecDel        = "recdel";        // button: ticket:recdel:{id}        (transcript)
    private const string ActionRecDelConfirm = "recdelconfirm"; // button: ticket:recdelconfirm:{id}
    private const string ActionRecDelCancel  = "recdelcancel";  // button: ticket:recdelcancel:{id}
    private const string ActionUnmask        = "unmask";        // button: ticket:unmask:{id}        (anonymous only)
    private const string ActionApproveLeave  = "approveleave";  // button: ticket:approveleave:{id}  (time-off only)
    private const string ActionLeaveModal    = "leaveapprove";  // modal:  ticket:leaveapprove:{id}
    private const string ModalLeaveStartId   = "start";
    private const string ModalLeaveEndId     = "end";

    private const string ModalSubjectId  = "subject";
    private const string ModalDetailsId  = "details";
    private const string ModalReasonId   = "reason";

    private const int MaxOpenTicketsPerOpener = 3;
    private const int MaxThreadMembersToAdd   = 50;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly TicketService _tickets;
    private readonly ILogger<TicketInteractionHandler> _logger;
    private readonly BotConfig _config;

    public TicketInteractionHandler(
        IServiceProvider services,
        DiscordSocketClient client,
        TicketService tickets,
        ILogger<TicketInteractionHandler> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _tickets  = tickets;
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

    /// <summary>The single "Delete Record" button attached to a transcript in
    /// the log channel. Public so TicketService can attach it when posting.</summary>
    public static MessageComponent BuildRecordButtons(int ticketId) =>
        new ComponentBuilder()
            .WithButton("Delete Record", $"ticket:{ActionRecDel}:{ticketId}", ButtonStyle.Danger, new Emoji("🗑️"))
            .Build();

    private static MessageComponent BuildControlButtons(int ticketId, bool isAnonymous, bool isTimeOff)
    {
        var builder = new ComponentBuilder()
            .WithButton("Claim",        $"ticket:{ActionClaim}:{ticketId}",    ButtonStyle.Primary,   new Emoji("🙋"))
            .WithButton("Set Priority", $"ticket:{ActionPrioMenu}:{ticketId}", ButtonStyle.Secondary, new Emoji("⚙️"))
            .WithButton("Close",        $"ticket:{ActionCloseBtn}:{ticketId}", ButtonStyle.Danger,    new Emoji("🔒"));

        // Time-off tickets get an Approve Leave control (assigns Reserve for a
        // date range); gated at click time to ticket staff.
        if (isTimeOff)
            builder.WithButton("Approve Leave", $"ticket:{ActionApproveLeave}:{ticketId}", ButtonStyle.Success, new Emoji("📆"));

        // Anonymous (Report-a-Member) tickets get an Unmask control, gated at
        // click time to TicketUnmaskRoleId / Administrator.
        if (isAnonymous)
            builder.WithButton("Unmask", $"ticket:{ActionUnmask}:{ticketId}", ButtonStyle.Secondary, new Emoji("🕵️"));

        return builder.Build();
    }

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
            else if (parts[1] == ActionLeaveModal && parts.Length == 3 && int.TryParse(parts[2], out var leaveId))
                await HandleLeaveApproveSubmitAsync(modal, leaveId);
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

        // Anonymous tickets relay through the opener's DM, so keep it to one
        // open anonymous ticket per person — that keeps the DM→ticket routing
        // unambiguous.
        if (category.AnonymousAllowed)
        {
            using var guardScope = _services.CreateScope();
            var guardDb = guardScope.ServiceProvider.GetRequiredService<BotDbContext>();
            var hasOpenAnon = await guardDb.SupportTickets.AnyAsync(t =>
                t.GuildId == guild.Id &&
                t.OpenerUserId == member.Id &&
                t.IsAnonymous &&
                t.Status != SupportTicketStatus.Closed);
            if (hasOpenAnon)
            {
                await modal.FollowupAsync(
                    "⛔ You already have an open anonymous report. Please wait for it to be resolved before opening another.",
                    ephemeral: true);
                return;
            }
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
        var embed = _tickets.BuildTicketEmbed(ticket, category);
        var ping  = routedRoleId != 0 ? $"<@&{routedRoleId}> — new ticket" : "New ticket";
        // Send with the transparent spacer attachment so the embed (which
        // references attachment://spacer.png) renders at full desktop width.
        using var spacer = TicketService.OpenSpacerStream();
        var isTimeOff = !string.IsNullOrWhiteSpace(_config.TicketTimeOffCategoryKey)
            && string.Equals(category.Key, _config.TicketTimeOffCategoryKey, StringComparison.OrdinalIgnoreCase);
        var control = await thread.SendFileAsync(
            spacer, TicketService.SpacerFileName,
            text: ping,
            embed: embed,
            components: BuildControlButtons(ticket.Id, ticket.IsAnonymous, isTimeOff),
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
            // Open a DM so the reporter has a channel to continue the (relayed)
            // conversation in. Best-effort — if their DMs are closed, the report
            // is still delivered; they just can't do two-way follow-up.
            var dmOk = await SendReporterIntroDmAsync(member, ticket);

            await modal.FollowupAsync(
                "✅ Your **anonymous** report has been submitted to HQ. Because it's anonymous "
                + "you won't see the ticket thread — HQ will handle it without seeing who filed it.\n\n"
                + (dmOk
                    ? "💬 I've sent you a DM — **reply there** to add more and to talk with HQ. Your identity stays hidden."
                    : "⚠️ I couldn't DM you, so two-way follow-up is off. Enable DMs from server members if you want to keep the conversation going."),
                ephemeral: true);
        }
        else
        {
            var link = $"https://discord.com/channels/{guild.Id}/{thread.Id}";
            await modal.FollowupAsync($"✅ Ticket **#{ticket.Id}** opened: {link}", ephemeral: true);
        }
    }

    private async Task<bool> SendReporterIntroDmAsync(SocketGuildUser reporter, SupportTicket ticket)
    {
        try
        {
            var dm = await reporter.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                $"🕵️ **Anonymous report #{ticket.Id} received.**\n"
                + "You can **reply to this DM** to add details or answer HQ — I'll relay your messages "
                + "into the report **without revealing who you are**. HQ's replies will show up here too.\n"
                + "_Reply here anytime while the report is open._",
                allowedMentions: AllowedMentions.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not DM anonymous reporter for ticket #{Id}", ticket.Id);
            return false;
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
            else if (TryParse(component.Data.CustomId, ActionRecDel, out var rdId))
                await HandleRecordDeletePromptAsync(component, rdId);
            else if (TryParse(component.Data.CustomId, ActionRecDelConfirm, out var rdcId))
                await HandleRecordDeleteConfirmAsync(component, rdcId);
            else if (TryParse(component.Data.CustomId, ActionRecDelCancel, out var rdxId))
                await HandleRecordDeleteCancelAsync(component, rdxId);
            else if (TryParse(component.Data.CustomId, ActionUnmask, out var umId))
                await HandleUnmaskAsync(component, umId);
            else if (TryParse(component.Data.CustomId, ActionApproveLeave, out var alId))
                await HandleApproveLeavePromptAsync(component, alId);
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

        var closerName = user.DisplayName ?? user.Username;
        var ok = await _tickets.CloseTicketAsync(ticket.Id, guild, user.Id, closerName, reason);

        await modal.FollowupAsync(
            ok ? $"✅ Ticket **#{ticket.Id}** closed. Transcript posted to the log channel."
               : "This ticket is already closed.",
            ephemeral: true);
    }

    // ─── Record deletion (HQ, from the transcript in the log channel) ──

    private async Task HandleRecordDeletePromptAsync(SocketMessageComponent component, int ticketId)
    {
        var user = component.User as SocketGuildUser;
        if (user is null || !HasHqPermission(user))
        {
            await component.RespondAsync("⛔ Only HQ can delete ticket records.", ephemeral: true);
            return;
        }

        // Swap the transcript's Delete button for a confirm/cancel pair in place.
        await component.UpdateAsync(p => p.Components = new ComponentBuilder()
            .WithButton("Confirm delete", $"ticket:{ActionRecDelConfirm}:{ticketId}", ButtonStyle.Danger, new Emoji("🗑️"))
            .WithButton("Cancel",         $"ticket:{ActionRecDelCancel}:{ticketId}",  ButtonStyle.Secondary)
            .Build());
    }

    private async Task HandleRecordDeleteCancelAsync(SocketMessageComponent component, int ticketId)
    {
        // Restore the original Delete button.
        await component.UpdateAsync(p => p.Components = BuildRecordButtons(ticketId));
    }

    private async Task HandleRecordDeleteConfirmAsync(SocketMessageComponent component, int ticketId)
    {
        var user = component.User as SocketGuildUser;
        if (user is null || !HasHqPermission(user))
        {
            await component.RespondAsync("⛔ Only HQ can delete ticket records.", ephemeral: true);
            return;
        }

        var guild = (component.Channel as SocketGuildChannel)?.Guild ?? user.Guild;

        await component.RespondAsync($"🗑️ Ticket **#{ticketId}** record permanently deleted.", ephemeral: true);
        await _tickets.DeleteTicketRecordAsync(ticketId, guild);

        // Remove the transcript message the button lived on.
        try { await component.Message.DeleteAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete transcript message for ticket #{Id}", ticketId); }

        _logger.LogInformation("Ticket #{Id} record deleted by {User}", ticketId, user.Username);
    }

    // ─── Unmask (reveal an anonymous reporter, gated + logged) ─────────

    private async Task HandleUnmaskAsync(SocketMessageComponent component, int ticketId)
    {
        var user = component.User as SocketGuildUser;
        if (user is null || !HasUnmaskPermission(user))
        {
            await component.RespondAsync("⛔ You don't have permission to unmask reporters.", ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) { await component.RespondAsync("Ticket not found.", ephemeral: true); return; }
        if (!ticket.IsAnonymous) { await component.RespondAsync("This ticket isn't anonymous.", ephemeral: true); return; }

        // Reveal only to the requester (ephemeral). The action is logged.
        await component.RespondAsync(
            $"🕵️ Reporter of ticket **#{ticket.Id}** ({ticket.AnonHandle}) is "
            + $"<@{ticket.OpenerUserId}> — **{ticket.OpenerDisplayName}**.\n"
            + "_This unmask was recorded in the bot logs. Handle with care._",
            ephemeral: true);

        _logger.LogWarning(
            "ANONYMOUS UNMASK: ticket #{Id} reporter {ReporterId} ({ReporterName}) revealed to {ByUser} ({ById})",
            ticket.Id, ticket.OpenerUserId, ticket.OpenerDisplayName, user.Username, user.Id);
    }

    private bool HasUnmaskPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        return _config.TicketUnmaskRoleId != 0 && user.Roles.Any(r => r.Id == _config.TicketUnmaskRoleId);
    }

    // ─── Time-off approval (assign Reserve for a date range) ───────────

    private async Task HandleApproveLeavePromptAsync(SocketMessageComponent component, int ticketId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) { await component.RespondAsync("Ticket not found.", ephemeral: true); return; }

        var user = component.User as SocketGuildUser;
        if (user is null || !HasStaffPermission(user, ticket))
        {
            await component.RespondAsync("⛔ Only the assigned team can approve leave.", ephemeral: true);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle(Truncate($"Approve Leave — Ticket #{ticketId}", 45))
            .WithCustomId($"ticket:{ActionLeaveModal}:{ticketId}")
            .AddTextInput("Start date (YYYY-MM-DD)", ModalLeaveStartId, TextInputStyle.Short,
                placeholder: "e.g. 2026-07-10", minLength: 8, maxLength: 10, required: true)
            .AddTextInput("End date (YYYY-MM-DD, last day of leave)", ModalLeaveEndId, TextInputStyle.Short,
                placeholder: "e.g. 2026-07-20", minLength: 8, maxLength: 10, required: true)
            .Build();

        await component.RespondWithModalAsync(modal);
    }

    private async Task HandleLeaveApproveSubmitAsync(SocketModal modal, int ticketId)
    {
        await modal.DeferAsync(ephemeral: true);

        var actor = modal.User as SocketGuildUser;
        var guild = (modal.Channel as SocketGuildChannel)?.Guild ?? actor?.Guild;
        if (actor is null || guild is null)
        {
            await modal.FollowupAsync("Could not resolve the server.", ephemeral: true);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null) { await modal.FollowupAsync("Ticket not found.", ephemeral: true); return; }
        if (!HasStaffPermission(actor, ticket))
        {
            await modal.FollowupAsync("⛔ Only the assigned team can approve leave.", ephemeral: true);
            return;
        }

        var fields = modal.Data.Components.ToDictionary(c => c.CustomId, c => c.Value ?? "");
        if (!TryParseLeaveDate(fields.GetValueOrDefault(ModalLeaveStartId), out var startDate) ||
            !TryParseLeaveDate(fields.GetValueOrDefault(ModalLeaveEndId), out var endDate))
        {
            await modal.FollowupAsync("⚠️ Couldn't read those dates. Use `YYYY-MM-DD` (e.g. 2026-07-10).", ephemeral: true);
            return;
        }
        if (endDate < startDate)
        {
            await modal.FollowupAsync("⚠️ The end date is before the start date.", ephemeral: true);
            return;
        }

        // Store end as exclusive (day after the last leave day) so the removal
        // sweep fires once the final day is fully over.
        var leaveStartUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var leaveEndUtc   = DateTime.SpecifyKind(endDate.AddDays(1), DateTimeKind.Utc);

        var reserveRole = guild.Roles.FirstOrDefault(r =>
            r.Name.Equals(_config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));
        if (reserveRole is null)
        {
            await modal.FollowupAsync(
                $"⚠️ Couldn't find the **{_config.ReserveRoleName}** role in this server.", ephemeral: true);
            return;
        }

        var member = guild.GetUser(ticket.OpenerUserId);
        if (member is null)
        {
            await modal.FollowupAsync("⚠️ The member who opened this ticket is no longer in the server.", ephemeral: true);
            return;
        }

        try
        {
            if (!member.Roles.Any(r => r.Id == reserveRole.Id))
                await member.AddRoleAsync(reserveRole);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to assign Reserve for leave on ticket #{Id}", ticket.Id);
            await modal.FollowupAsync(
                "⚠️ Couldn't assign the Reserve role — the bot's highest role may be below Reserve. "
                + "Fix the role order and try again.", ephemeral: true);
            return;
        }

        ticket.LeaveStartUtc   = leaveStartUtc;
        ticket.LeaveEndUtc     = leaveEndUtc;
        ticket.ReserveAssigned = true;
        ticket.LastActivityUtc = DateTime.UtcNow;
        db.SupportTicketMessages.Add(SystemMessage(ticket, actor,
            $"Leave approved {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}; Reserve assigned", DateTime.UtcNow));
        await db.SaveChangesAsync();

        // Update the control embed (adds the "On leave" field) and post a note.
        if (modal.Channel is SocketThreadChannel thread)
        {
            try
            {
                var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == ticket.CategoryKey);
                if (await thread.GetMessageAsync(ticket.ControlMessageId) is IUserMessage control)
                    await control.ModifyAsync(p => p.Embed = _tickets.BuildTicketEmbed(ticket, category));

                await thread.SendMessageAsync(
                    $"📆 Leave approved by {actor.Mention}: **{startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}**.\n"
                    + $"{member.Mention} has been given **{reserveRole.Name}** (AWOL-exempt) for the window — "
                    + "I'll remove it automatically when they're back.",
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not post leave-approval note for ticket #{Id}", ticket.Id); }
        }

        // DM the member.
        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                $"✅ Your time-off request (ticket #{ticket.Id}) is **approved** for "
                + $"**{startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}**. You've been set to "
                + $"**{reserveRole.Name}**, so you won't be flagged AWOL while you're out. "
                + "Enjoy your break!",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex) { _logger.LogInformation(ex, "Could not DM leave approval to member for ticket #{Id}", ticket.Id); }

        await modal.FollowupAsync(
            $"✅ Leave approved for {member.DisplayName} ({startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}). "
            + "Reserve assigned; it'll be removed automatically when the window ends.",
            ephemeral: true);
    }

    private static bool TryParseLeaveDate(string? raw, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
    }

    // ─── Message capture (transcript + activity) ──────────────────────

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        try
        {
            if (message.Author.IsBot) return;
            if (message is not SocketUserMessage) return;

            // Reporter DM → relayed into their open anonymous ticket thread.
            if (message.Channel is IDMChannel)
            {
                await HandleReporterDmAsync(message);
                return;
            }

            // Ticket-thread message → capture for the transcript (+ relay staff
            // messages out to an anonymous reporter's DM).
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

            // Anonymous ticket: the reporter isn't in the thread, so every human
            // message here is staff — relay it to the reporter's DM.
            if (ticket.IsAnonymous && direction == SupportTicketMessageDirection.FromStaff)
                await RelayStaffToReporterAsync(ticket, message.Content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to capture ticket thread message");
        }
    }

    /// <summary>
    /// A DM from a member who has an open anonymous ticket → relayed into that
    /// ticket's staff thread under their anon handle. If they have no open
    /// anonymous ticket, the DM is ignored (other DM handlers may own it).
    /// </summary>
    private async Task HandleReporterDmAsync(SocketMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Content)) return;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t =>
            t.OpenerUserId == message.Author.Id &&
            t.IsAnonymous &&
            t.Status != SupportTicketStatus.Closed);
        if (ticket is null) return; // not a relay DM

        var guild = _client.GetGuild(ticket.GuildId);
        if (guild is null) return;

        IThreadChannel? thread = guild.GetChannel(ticket.ThreadId) as IThreadChannel;
        if (thread is null)
        {
            try { thread = await _client.Rest.GetChannelAsync(ticket.ThreadId) as IThreadChannel; }
            catch { /* thread gone */ }
        }

        if (thread is IMessageChannel msgChannel)
        {
            await msgChannel.SendMessageAsync(
                $"🕵️ **{ticket.AnonHandle}:** {Truncate(message.Content, 1800)}",
                allowedMentions: AllowedMentions.None);
        }

        db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            TicketId          = ticket.Id,
            AuthorUserId      = message.Author.Id,
            AuthorDisplayName = ticket.AnonHandle ?? "Anonymous",
            Content           = Truncate(message.Content, 4000),
            SentUtc           = message.Timestamp.UtcDateTime,
            Direction         = SupportTicketMessageDirection.FromOpener,
        });
        ticket.LastActivityUtc = message.Timestamp.UtcDateTime;
        await db.SaveChangesAsync();

        // Acknowledge delivery so the reporter knows it went through.
        try { await message.AddReactionAsync(new Emoji("✅")); }
        catch { /* reaction is best-effort */ }
    }

    /// <summary>Relays a staff thread message out to the anonymous reporter's DM.</summary>
    private async Task RelayStaffToReporterAsync(SupportTicket ticket, string content)
    {
        var guild = _client.GetGuild(ticket.GuildId);
        var reporter = guild?.GetUser(ticket.OpenerUserId);
        if (reporter is null) return;

        try
        {
            var dm = await reporter.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                $"🛡️ **HQ (report #{ticket.Id}):** {Truncate(content, 1800)}",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not relay staff reply to anonymous reporter for ticket #{Id}", ticket.Id);
        }
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
            if (m.IsBot) continue; // don't drag DISBOARD et al. into ticket threads
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
                await control.ModifyAsync(p => p.Embed = _tickets.BuildTicketEmbed(ticket, category));
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

    /// <summary>Gate for destructive record deletion — Administrator or the HQ role.</summary>
    private bool HasHqPermission(SocketGuildUser user)
    {
        if (user.GuildPermissions.Administrator) return true;
        return _config.TicketHqRoleId != 0 && user.Roles.Any(r => r.Id == _config.TicketHqRoleId);
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
