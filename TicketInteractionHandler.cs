using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
/// filed it. Follow-up is a DM relay: only DMs sent as a Discord reply to one
/// of the bot's report DMs are relayed, so answers to other DM wizards
/// (e.g. /gamertags) never land in the staff thread.
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

    // Report number in the bot's own report DMs: the intro
    // ("🕵️ **Anonymous report #12 received.**") and HQ relays ("🛡️ **HQ (report #12):** …").
    private static readonly Regex RelayDmReportId =
        new(@"^\S+ \*\*(?:Anonymous report|HQ \(report) #(\d+)", RegexOptions.Compiled);

    private static readonly TimeSpan RelayHintInterval = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<ulong, DateTime> _relayHintSentUtc = new();

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

    /// <summary>The buttons attached to a transcript in the log channel: "Delete
    /// Record", plus "Unmask" on anonymous tickets since the transcript never
    /// names the reporter. Public so TicketService can attach them when posting.</summary>
    public static MessageComponent BuildRecordButtons(int ticketId, bool isAnonymous)
    {
        var builder = new ComponentBuilder()
            .WithButton("Delete Record", $"ticket:{ActionRecDel}:{ticketId}", ButtonStyle.Danger, new Emoji("🗑️"));

        if (isAnonymous)
            builder.WithButton("Unmask", $"ticket:{ActionUnmask}:{ticketId}", ButtonStyle.Secondary, new Emoji("🕵️"));

        return builder.Build();
    }

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
            await ResetPanelSelectAsync(component);
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
                await ResetPanelSelectAsync(component);
                return;
            }
        }

        var anonNote = category.AnonymousAllowed
            ? "This category is anonymous — HQ won't see who submitted it."
            : $"Category: {category.Label}";

        var isTimeOff = IsTimeOffCategory(category);

        var builder = new ModalBuilder()
            .WithTitle(Truncate($"New Ticket — {category.Label}", 45))
            .WithCustomId($"ticket:{ActionCreate}:{category.Key}")
            .AddTextInput(
                label: isTimeOff ? "Reason for time off" : "Subject",
                customId: ModalSubjectId,
                style: TextInputStyle.Short,
                placeholder: isTimeOff ? "e.g. vacation, exams, work travel" : "A short summary of what you need",
                minLength: 3, maxLength: 100, required: true);

        if (isTimeOff)
        {
            // Member supplies the dates they need so HQ sees them up front.
            builder
                .AddTextInput("First day away (YYYY-MM-DD)", ModalLeaveStartId, TextInputStyle.Short,
                    placeholder: "e.g. 2026-07-10", minLength: 8, maxLength: 10, required: true)
                .AddTextInput("Last day away (YYYY-MM-DD)", ModalLeaveEndId, TextInputStyle.Short,
                    placeholder: "e.g. 2026-07-20", minLength: 8, maxLength: 10, required: true)
                .AddTextInput("Anything else? (optional)", ModalDetailsId, TextInputStyle.Paragraph,
                    placeholder: "Any extra context for HQ", minLength: 0, maxLength: 1000, required: false);
        }
        else
        {
            builder.AddTextInput(
                label: "Details",
                customId: ModalDetailsId,
                style: TextInputStyle.Paragraph,
                placeholder: anonNote,
                minLength: 10, maxLength: 1500, required: true);
        }

        await component.RespondWithModalAsync(builder.Build());

        // Reset the panel dropdown back to its placeholder. Discord won't fire a
        // new event if the user re-picks the same option, and it sends no event
        // when a modal is dismissed — so without this, dismissing the modal
        // leaves the category "selected" and un-clickable. Editing the message's
        // components clears the selection for everyone.
        await ResetPanelSelectAsync(component);
    }

    /// <summary>Re-renders the panel's select menu so no option stays selected.</summary>
    private async Task ResetPanelSelectAsync(SocketMessageComponent component)
    {
        try
        {
            var cats = _config.GetTicketCategories();
            await component.Message.ModifyAsync(m => m.Components = BuildPanelComponents(cats));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not reset ticket panel select");
        }
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

        // Time-off tickets carry the member's requested dates. Store them as the
        // (not-yet-approved) window so HQ sees them and the Approve modal
        // pre-fills; the sweep does nothing until HQ approves (LeaveScheduled).
        DateTime? reqStart = null, reqEnd = null;
        if (IsTimeOffCategory(category))
        {
            var startRaw = fields.GetValueOrDefault(ModalLeaveStartId, "").Trim();
            var endRaw   = fields.GetValueOrDefault(ModalLeaveEndId, "").Trim();
            if (TryParseLeaveDate(startRaw, out var s)) reqStart = DateTime.SpecifyKind(s, DateTimeKind.Utc);
            if (TryParseLeaveDate(endRaw, out var e))   reqEnd   = DateTime.SpecifyKind(e, DateTimeKind.Utc);

            // Always surface what they typed, even if it didn't parse cleanly.
            var reqLine = $"📅 Requested dates: {(startRaw.Length > 0 ? startRaw : "?")} → {(endRaw.Length > 0 ? endRaw : "?")}";
            details = string.IsNullOrEmpty(details) ? reqLine : $"{reqLine}\n\n{details}";
        }

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
            LeaveStartUtc     = reqStart, // requested (pending approval); LeaveScheduled stays false
            LeaveEndUtc       = reqEnd,
        };
        if (ticket.IsAnonymous)
            ticket.AnonHandle = $"Anonymous #{Guid.NewGuid().ToString("N")[..4]}";

        db.SupportTickets.Add(ticket);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (category.AnonymousAllowed)
        {
            // Hit the one-open-anonymous-ticket unique index (the atomic backstop
            // for a race that beat the pre-check above). Nothing was persisted.
            await modal.FollowupAsync(
                "⛔ You already have an open anonymous report. Please wait for it to be resolved before opening another.",
                ephemeral: true);
            return;
        }

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
            // Roll back the row we just inserted so it can't become an orphan
            // (ThreadId=0) that counts against the open-ticket cap and gets
            // picked up by the escalation/auto-close sweeps.
            try { db.SupportTickets.Remove(ticket); await db.SaveChangesAsync(); }
            catch (Exception rex) { _logger.LogWarning(rex, "Could not roll back orphan ticket #{Id}", ticket.Id); }
            await modal.FollowupAsync(
                "⚠️ Couldn't create your ticket thread — the bot may be missing the "
                + "\"Create Private Threads\" permission in the tickets channel. Please ping an officer.",
                ephemeral: true);
            return;
        }

        // Add the opener (identified only) + the routed role's members. An
        // anonymous reporter who holds the routed role is skipped, or they'd
        // show in their own report's member list.
        if (!ticket.IsAnonymous)
        {
            try { await thread.AddUserAsync(member); } catch (Exception ex)
            { _logger.LogWarning(ex, "Could not add opener to ticket thread #{Id}", ticket.Id); }
        }
        await AddRoleMembersToThreadAsync(thread, guild, routedRoleId,
            excludeUserId: ticket.IsAnonymous ? member.Id : null);

        // Post the ticket embed with control buttons.
        var embed = _tickets.BuildTicketEmbed(ticket, category);
        var ping  = routedRoleId != 0 ? $"<@&{routedRoleId}> — new ticket" : "New ticket";
        // Send with the transparent spacer attachment so the embed (which
        // references attachment://spacer.png) renders at full desktop width.
        using var spacer = TicketService.OpenSpacerStream();
        var isTimeOff = IsTimeOffCategory(category);
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

        // Privacy warning. Discord adds every member of a mentioned role (roles
        // under 100 members) to the thread it was mentioned in — private threads
        // included — so a single role ping in here silently exposes the whole
        // ticket to that role. We can't block what staff type, so we warn.
        // See: https://support.discord.com/hc/en-us/articles/4403205878423-Threads-FAQ
        try
        {
            await thread.SendMessageAsync(
                "⚠️ **Heads up — this is a private thread.** Pinging a **role** in here will add "
                + "*every member of that role* to this thread and they'll be able to read the whole "
                + "ticket. Same goes for @mentioning a user. If you need to loop someone in, do it "
                + "deliberately — otherwise link them out to another channel instead.",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not post privacy warning in ticket thread #{Id}", ticket.Id);
        }

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

        if (ticket.IsAnonymous)
            _logger.LogInformation(
                "Ticket #{Id} ({Category}, anonymous as {Handle}) opened → thread {ThreadId}",
                ticket.Id, category.Key, ticket.AnonHandle, thread.Id);
        else
            _logger.LogInformation(
                "Ticket #{Id} ({Category}) opened by {User} → thread {ThreadId}",
                ticket.Id, category.Key, member.Username, thread.Id);

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
                    ? "💬 I've sent you a DM — use Discord's **Reply** on that message to add more and to talk with HQ. Your identity stays hidden."
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
                + "To add details or answer HQ, use Discord's **Reply** on this message (or on any HQ "
                + "message I send you) — I'll relay it into the report **without revealing who you are**. "
                + "HQ's replies will show up here too.\n"
                + "_Only replies to my report messages are relayed. Anything else you DM me (like "
                + "answers to other bot commands) is never forwarded to HQ._",
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
        var claimant = user.DisplayName ?? user.Username;

        // Atomic claim: only succeeds if still unclaimed, so two simultaneous
        // clicks can't both "win" and post a claim message.
        var rows = await db.SupportTickets
            .Where(t => t.Id == ticketId && t.ClaimedByUserId == null && t.Status != SupportTicketStatus.Closed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.ClaimedByUserId, (ulong?)user.Id)
                .SetProperty(t => t.ClaimedByUsername, claimant)
                .SetProperty(t => t.ClaimedAtUtc, (DateTime?)now)
                .SetProperty(t => t.Status, SupportTicketStatus.InProgress)
                .SetProperty(t => t.LastActivityUtc, now));
        if (rows == 0)
        {
            await component.FollowupAsync("Someone else just claimed this ticket.", ephemeral: true);
            return;
        }

        var fresh = await db.SupportTickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId);
        db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            TicketId = ticketId, AuthorUserId = user.Id, AuthorDisplayName = claimant,
            Content = $"Claimed by {claimant}", SentUtc = now, Direction = SupportTicketMessageDirection.System,
        });
        await db.SaveChangesAsync();

        if (fresh is not null) await UpdateControlEmbedAsync(component, fresh);
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
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var isAnonymous = await db.SupportTickets
            .Where(t => t.Id == ticketId)
            .Select(t => t.IsAnonymous)
            .FirstOrDefaultAsync();

        // Restore the original record buttons.
        await component.UpdateAsync(p => p.Components = BuildRecordButtons(ticketId, isAnonymous));
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

        // Reveal only to the requester (ephemeral). The action is logged + audited.
        await component.RespondAsync(
            $"🕵️ Reporter of ticket **#{ticket.Id}** ({ticket.AnonHandle}) is "
            + $"<@{ticket.OpenerUserId}> — **{ticket.OpenerDisplayName}**.\n"
            + "_This unmask was recorded in the ticket log. Handle with care._",
            ephemeral: true);

        _logger.LogWarning(
            "ANONYMOUS UNMASK: ticket #{Id} reporter {ReporterId} ({ReporterName}) revealed to {ByUser} ({ById})",
            ticket.Id, ticket.OpenerUserId, ticket.OpenerDisplayName, user.Username, user.Id);

        // Audit post to the HQ log channel so unmasks are visible to other
        // officers, not just buried in bot logs.
        if (_config.TicketLogChannelId != 0
            && (component.Channel as SocketGuildChannel)?.Guild is { } g
            && g.GetTextChannel(_config.TicketLogChannelId) is { } logChannel)
        {
            try
            {
                await logChannel.SendMessageAsync(
                    $"🕵️ **Unmask** — {user.Mention} revealed the reporter of ticket **#{ticket.Id}** "
                    + $"({ticket.AnonHandle}) as <@{ticket.OpenerUserId}>.",
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not post unmask audit for ticket #{Id}", ticket.Id); }
        }
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

        // Pre-fill with the dates the member requested at creation (if any) so
        // HQ can just confirm or tweak them.
        var startDefault = ticket.LeaveStartUtc?.ToString("yyyy-MM-dd");
        var endDefault   = ticket.LeaveEndUtc?.ToString("yyyy-MM-dd");

        var modal = new ModalBuilder()
            .WithTitle(Truncate($"Approve Leave — Ticket #{ticketId}", 45))
            .WithCustomId($"ticket:{ActionLeaveModal}:{ticketId}")
            .AddTextInput("Start date (YYYY-MM-DD)", ModalLeaveStartId, TextInputStyle.Short,
                placeholder: "e.g. 2026-07-10", minLength: 8, maxLength: 10, required: true, value: startDefault)
            .AddTextInput("End date (YYYY-MM-DD, last day of leave)", ModalLeaveEndId, TextInputStyle.Short,
                placeholder: "e.g. 2026-07-20", minLength: 8, maxLength: 10, required: true, value: endDefault)
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
        if (_config.TicketMaxLeaveDays > 0 && (endDate - startDate).TotalDays + 1 > _config.TicketMaxLeaveDays)
        {
            await modal.FollowupAsync(
                $"⚠️ That leave is longer than the max of **{_config.TicketMaxLeaveDays} days**. "
                + "Double-check the dates (or split it into separate requests).",
                ephemeral: true);
            return;
        }

        // Store dates inclusive (start day and last leave day, both at 00:00 UTC).
        // The removal sweep fires after the end day is fully over (end + 1 day).
        var leaveStartUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var leaveEndUtc   = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

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

        // Fully date-driven: only apply Reserve now if the leave has already
        // started; otherwise the maintenance sweep applies it on the start date.
        var now = DateTime.UtcNow;
        var startNow = leaveStartUtc <= now;

        if (startNow)
        {
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
        }

        ticket.LeaveStartUtc   = leaveStartUtc;
        ticket.LeaveEndUtc     = leaveEndUtc;
        ticket.LeaveScheduled  = true;
        ticket.ReserveAssigned = startNow;
        ticket.LastActivityUtc = now;

        // Approving is handling the ticket — mark it in-progress (and claim it if
        // nobody has) so the escalation sweep stops treating it as unanswered.
        if (ticket.Status == SupportTicketStatus.Open)
            ticket.Status = SupportTicketStatus.InProgress;
        if (ticket.ClaimedByUserId is null)
        {
            ticket.ClaimedByUserId   = actor.Id;
            ticket.ClaimedByUsername = actor.DisplayName ?? actor.Username;
            ticket.ClaimedAtUtc      = now;
        }

        db.SupportTicketMessages.Add(SystemMessage(ticket, actor,
            $"Leave approved {startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd} "
            + (startNow ? "(active now)" : "(starts on date)"), now));
        await db.SaveChangesAsync();

        var whenNote = startNow
            ? $"{member.Mention} has been given **{reserveRole.Name}** (AWOL-exempt) now through the window — I'll remove it automatically when they're back."
            : $"{member.Mention} will be given **{reserveRole.Name}** (AWOL-exempt) automatically on **{startDate:yyyy-MM-dd}**, and it'll be removed when the window ends.";

        if (modal.Channel is SocketThreadChannel thread)
        {
            try
            {
                var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == ticket.CategoryKey);
                if (await thread.GetMessageAsync(ticket.ControlMessageId) is IUserMessage control)
                    await control.ModifyAsync(p => p.Embed = _tickets.BuildTicketEmbed(ticket, category));

                await thread.SendMessageAsync(
                    $"📆 Leave approved by {actor.Mention}: **{startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}**.\n{whenNote}",
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not post leave-approval note for ticket #{Id}", ticket.Id); }
        }

        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync(
                $"✅ Your time-off request (ticket #{ticket.Id}) is **approved** for "
                + $"**{startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}**.\n"
                + (startNow
                    ? $"You've been set to **{reserveRole.Name}** now, so you won't be flagged AWOL while you're out. Enjoy your break!"
                    : $"You'll be set to **{reserveRole.Name}** automatically on **{startDate:yyyy-MM-dd}** and returned to normal when it ends."),
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex) { _logger.LogInformation(ex, "Could not DM leave approval to member for ticket #{Id}", ticket.Id); }

        await modal.FollowupAsync(
            $"✅ Leave approved for {member.DisplayName} ({startDate:yyyy-MM-dd} → {endDate:yyyy-MM-dd}). "
            + (startNow ? "Reserve applied now" : $"Reserve will be applied on {startDate:yyyy-MM-dd}")
            + "; it's removed automatically when the window ends.",
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
            if (message is not SocketUserMessage userMessage) return;

            // Reporter DM → relayed into their open anonymous ticket thread.
            if (message.Channel is IDMChannel)
            {
                await HandleReporterDmAsync(userMessage);
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
    /// A DM sent as a Discord reply to one of the bot's report DMs (the intro or
    /// an HQ relay) → relayed into that report's staff thread under the anon
    /// handle. Any other DM is left alone: DM wizards (/gamertags, /event, …)
    /// see the same messages, and relaying them would expose the reporter.
    /// </summary>
    private async Task HandleReporterDmAsync(SocketUserMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Content)) return;

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var reportId = await GetRepliedReportIdAsync(message);
        if (reportId is null)
        {
            await MaybeSendRelayHintAsync(message, db);
            return;
        }

        var ticket = await db.SupportTickets
            .FirstOrDefaultAsync(t => t.Id == reportId.Value
                                   && t.OpenerUserId == message.Author.Id
                                   && t.IsAnonymous);
        if (ticket is null) return;

        if (ticket.Status == SupportTicketStatus.Closed)
        {
            try
            {
                await message.Channel.SendMessageAsync(
                    $"🔒 Report **#{ticket.Id}** is closed, so that message wasn't sent to HQ. "
                    + "Open a new report from the ticket panel if you need to follow up.",
                    allowedMentions: AllowedMentions.None);
            }
            catch { /* best-effort */ }
            return;
        }

        var guild = _client.GetGuild(ticket.GuildId);
        if (guild is null) return;

        IThreadChannel? thread = guild.GetChannel(ticket.ThreadId) as IThreadChannel;
        if (thread is null)
        {
            try { thread = await _client.Rest.GetChannelAsync(ticket.ThreadId) as IThreadChannel; }
            catch { /* thread gone */ }
        }

        var delivered = false;
        if (thread is IMessageChannel msgChannel)
        {
            try
            {
                await msgChannel.SendMessageAsync(
                    $"🕵️ **{ticket.AnonHandle}:** {Truncate(message.Content, 1800)}",
                    allowedMentions: AllowedMentions.None);
                delivered = true;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not post relayed reporter message to thread for ticket #{Id}", ticket.Id); }
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

        // Acknowledge to the reporter: ✅ if it reached the thread, ⚠️ if it was
        // only recorded (thread temporarily unreachable) so they know to retry.
        try { await message.AddReactionAsync(new Emoji(delivered ? "✅" : "⚠️")); }
        catch { /* reaction is best-effort */ }
    }

    /// <summary>
    /// The report number of the bot DM this message replies to, or null when it
    /// isn't a reply to one of the bot's report DMs.
    /// </summary>
    private async Task<int?> GetRepliedReportIdAsync(SocketUserMessage message)
    {
        IMessage? replied = message.ReferencedMessage;
        if (replied is null && message.Reference?.MessageId.IsSpecified == true)
        {
            try { replied = await message.Channel.GetMessageAsync(message.Reference.MessageId.Value); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not fetch the DM a reporter replied to"); }
        }

        if (replied is null || replied.Author.Id != _client.CurrentUser.Id) return null;

        var match = RelayDmReportId.Match(replied.Content ?? string.Empty);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// Tells a reporter with an open anonymous report that a plain DM wasn't
    /// relayed. Rate-limited per user, since the DM may belong to another wizard.
    /// </summary>
    private async Task MaybeSendRelayHintAsync(SocketUserMessage message, BotDbContext db)
    {
        var now = DateTime.UtcNow;
        if (_relayHintSentUtc.TryGetValue(message.Author.Id, out var last) && now - last < RelayHintInterval)
            return;

        var openReportId = await db.SupportTickets
            .Where(t => t.OpenerUserId == message.Author.Id
                     && t.IsAnonymous
                     && t.Status != SupportTicketStatus.Closed)
            .OrderByDescending(t => t.CreatedUtc)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync();
        if (openReportId is null) return;

        _relayHintSentUtc[message.Author.Id] = now;
        try
        {
            await message.Channel.SendMessageAsync(
                $"💡 To send something to HQ for anonymous report **#{openReportId}**, use Discord's **Reply** "
                + "on one of my report messages. Other DMs aren't forwarded.",
                allowedMentions: AllowedMentions.None);
        }
        catch { /* best-effort */ }
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
                $"🛡️ **HQ (report #{ticket.Id}):** {Truncate(content, 1800)}\n"
                + "_↩️ Use **Reply** on this message to answer._",
                allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not relay staff reply to anonymous reporter for ticket #{Id}", ticket.Id);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private async Task AddRoleMembersToThreadAsync(
        SocketThreadChannel thread, SocketGuild guild, ulong roleId, ulong? excludeUserId = null)
    {
        if (roleId == 0) return;
        var role = guild.GetRole(roleId);
        if (role is null) return;

        var added = 0;
        foreach (var m in role.Members)
        {
            if (m.IsBot) continue; // don't drag DISBOARD et al. into ticket threads
            if (m.Id == excludeUserId) continue;
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

    private bool IsTimeOffCategory(TicketCategoryDef category) =>
        !string.IsNullOrWhiteSpace(_config.TicketTimeOffCategoryKey)
        && string.Equals(category.Key, _config.TicketTimeOffCategoryKey, StringComparison.OrdinalIgnoreCase);

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
