using ClanGuardBot.Data;
using ClanGuardBot.Handlers;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Periodic maintenance sweep for the ticket system. Two jobs per cycle,
/// for every non-closed ticket:
///
///   1. **Keep-alive** — Discord auto-archives an inactive thread after at
///      most 1 week (its hard cap; there's no 2-week option). To keep an
///      open ticket visible for the full TicketAutoCloseInactivityDays window,
///      the sweep un-archives any still-open ticket thread that Discord has
///      archived but that hasn't yet hit the inactivity threshold.
///
///   2. **Auto-close** — once a ticket has gone TicketAutoCloseInactivityDays
///      without activity, it's closed via TicketService (transcript to the log
///      channel, embed flipped, thread archived + locked), exactly as if an
///      officer had pressed Close.
///
/// Idle entirely when TicketAutoCloseInactivityDays == 0. Mirrors the
/// BackgroundService + wait-for-connected pattern used by AwolCheckService.
/// </summary>
public class TicketMaintenanceService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly TicketService _tickets;
    private readonly ILogger<TicketMaintenanceService> _logger;
    private readonly BotConfig _config;

    public TicketMaintenanceService(
        IServiceProvider services,
        DiscordSocketClient client,
        TicketService tickets,
        ILogger<TicketMaintenanceService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _tickets  = tickets;
        _logger   = logger;
        _config   = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.TicketMaintenanceIntervalMinutes));
        _logger.LogInformation(
            "Ticket maintenance started. Interval={Interval}m, inactivity window={Days}d (0=off), "
            + "leave-removal always on.",
            (int)interval.TotalMinutes, _config.TicketAutoCloseInactivityDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during ticket maintenance sweep");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // ── 1. Manage approved leaves (independent of the auto-close setting) ──
        // Applies Reserve on the start date and removes it after the end date.
        List<int> managedLeaveIds;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            managedLeaveIds = await db.SupportTickets
                .Where(t => t.LeaveScheduled)
                .Select(t => t.Id)
                .ToListAsync(ct);
        }
        foreach (var id in managedLeaveIds)
        {
            if (ct.IsCancellationRequested) break;
            await ManageLeaveAsync(id);
        }

        // ── 2. Escalate unanswered (unclaimed) tickets (always-on if configured) ──
        if (_config.TicketFirstEscalationHours > 0 || _config.TicketSecondEscalationHours > 0)
        {
            List<int> unclaimedIds;
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
                unclaimedIds = await db.SupportTickets
                    .Where(t => t.Status == SupportTicketStatus.Open && t.EscalationLevel < 2)
                    .Select(t => t.Id)
                    .ToListAsync(ct);
            }
            foreach (var id in unclaimedIds)
            {
                if (ct.IsCancellationRequested) break;
                await EscalateIfDueAsync(id);
            }
        }

        // ── 3. Keep-alive + auto-close (only when enabled) ──
        var cutoffDays = _config.TicketAutoCloseInactivityDays;
        if (cutoffDays <= 0) return;

        List<SupportTicket> open;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            open = await db.SupportTickets
                .Where(t => t.Status != SupportTicketStatus.Closed)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        foreach (var ticket in open)
        {
            if (ct.IsCancellationRequested) break;

            var guild = _client.GetGuild(ticket.GuildId);
            if (guild is null) continue;

            var idleDays = (now - ticket.LastActivityUtc).TotalDays;

            if (idleDays >= cutoffDays)
            {
                await _tickets.CloseTicketAsync(
                    ticket.Id, guild,
                    _client.CurrentUser.Id,
                    "Auto-close",
                    $"Auto-closed after {cutoffDays} days of inactivity.");
            }
            else
            {
                await KeepAliveAsync(guild, ticket);
            }
        }
    }

    /// <summary>
    /// Drives an approved leave through its window: applies the Reserve role once
    /// the start date arrives, and removes it (plus resets the member's AWOL
    /// activity window, same mechanism RankTrackingHandler uses on AWOL removal)
    /// once the end date passes. Clears LeaveScheduled when done so it isn't
    /// re-processed, even if the member/role is gone.
    /// </summary>
    private async Task ManageLeaveAsync(int ticketId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
            if (ticket is null || !ticket.LeaveScheduled) return;

            var guild = _client.GetGuild(ticket.GuildId);
            if (guild is null) return; // bot not ready for this guild; retry next cycle

            var now = DateTime.UtcNow;
            var member = guild.GetUser(ticket.OpenerUserId);
            var reserveRole = guild.Roles.FirstOrDefault(r =>
                r.Name.Equals(_config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));

            // ── End: window has passed → remove Reserve, reset window, finish ──
            // LeaveEndUtc is the inclusive last day (00:00 UTC), so the leave is
            // over once we're past the end of that day (end + 1 day).
            if (ticket.LeaveEndUtc is { } end && now >= end.AddDays(1))
            {
                // Only consider the removal "done" if the role is actually off
                // the member (or the member/role is genuinely gone). If the
                // RemoveRoleAsync call fails — e.g. the bot's role got moved
                // below Reserve — we must NOT clear LeaveScheduled, or the member
                // would be stuck in Reserve forever. Leave it scheduled to retry.
                var removed = true;
                if (member is not null && reserveRole is not null && member.Roles.Any(r => r.Id == reserveRole.Id))
                {
                    try { await member.RemoveRoleAsync(reserveRole); }
                    catch (Exception ex)
                    {
                        removed = false;
                        _logger.LogWarning(ex, "Could not remove Reserve for ended leave (ticket #{Id}) — will retry", ticketId);
                    }
                }

                if (!removed) return; // retry next cycle; keep LeaveScheduled set

                if (member is not null)
                {
                    var activity = await db.UserActivities
                        .FirstOrDefaultAsync(a => a.GuildId == guild.Id && a.UserId == member.Id);
                    if (activity is null)
                    {
                        activity = new UserActivity { GuildId = guild.Id, UserId = member.Id, Username = member.ToString() ?? member.Username };
                        db.UserActivities.Add(activity);
                    }
                    activity.WindowResetAt = now;
                }

                ticket.ReserveAssigned = false;
                ticket.LeaveScheduled  = false;
                await db.SaveChangesAsync();
                _logger.LogInformation("Leave ended for ticket #{Id}; Reserve removed, window reset", ticketId);

                await _tickets.RefreshControlEmbedAsync(ticket, guild);
                if (member is not null)
                    await TryDmAsync(member,
                        $"👋 Welcome back! Your time-off (ticket #{ticketId}) has ended and your "
                        + $"**{_config.ReserveRoleName}** status has been removed. You've got a fresh "
                        + "activity window, so no worries about being flagged.");
                return;
            }

            // ── Start: window has begun but Reserve not yet applied → apply it ──
            if (!ticket.ReserveAssigned && ticket.LeaveStartUtc is { } start && now >= start)
            {
                if (member is null || reserveRole is null)
                {
                    _logger.LogWarning("Cannot start leave for ticket #{Id}: member or Reserve role missing", ticketId);
                    return; // retry next cycle
                }

                try
                {
                    if (!member.Roles.Any(r => r.Id == reserveRole.Id))
                        await member.AddRoleAsync(reserveRole);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not apply Reserve at leave start (ticket #{Id})", ticketId);
                    return; // retry next cycle
                }

                ticket.ReserveAssigned = true;
                await db.SaveChangesAsync();
                _logger.LogInformation("Leave started for ticket #{Id}; Reserve applied", ticketId);

                await _tickets.RefreshControlEmbedAsync(ticket, guild);
                await TryDmAsync(member,
                    $"🟢 Your approved time-off (ticket #{ticketId}) has started — you're now set to "
                    + $"**{reserveRole.Name}** and won't be flagged AWOL until it ends.");
            }
            // else: approved but start date not reached yet — nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error managing leave for ticket #{Id}", ticketId);
        }
    }

    private async Task TryDmAsync(SocketGuildUser member, string content)
    {
        try
        {
            var dm = await member.CreateDMChannelAsync();
            await dm.SendMessageAsync(content, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not DM member {UserId} for leave update", member.Id);
        }
    }

    /// <summary>
    /// Escalates a still-unclaimed ticket. First threshold: bump priority one
    /// level and re-ping the routed role in the thread. Second threshold: set
    /// priority to Urgent and ping TicketEscalationRoleId (or the routed role if
    /// none is configured), pulling that role's members into the thread first.
    /// EscalationLevel is stamped so each stage fires once; a ticket that's blown
    /// past both thresholds (e.g. after downtime) jumps straight to stage 2.
    /// </summary>
    private async Task EscalateIfDueAsync(int ticketId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
            if (ticket is null || ticket.Status != SupportTicketStatus.Open || ticket.EscalationLevel >= 2)
                return;
            if (ticket.ThreadId == 0) return; // orphan with no thread — nothing to escalate

            var now = DateTime.UtcNow;
            var ageHours = (now - ticket.CreatedUtc).TotalHours;
            var first  = _config.TicketFirstEscalationHours;
            var second = _config.TicketSecondEscalationHours;

            var doSecond = ticket.EscalationLevel < 2 && second > 0 && ageHours >= second;
            var doFirst  = !doSecond && ticket.EscalationLevel < 1 && first > 0 && ageHours >= first;
            if (!doSecond && !doFirst) return;

            var guild = _client.GetGuild(ticket.GuildId);
            if (guild is null) return;

            var thread = guild.GetChannel(ticket.ThreadId) as IThreadChannel;
            if (thread is null)
            {
                try { thread = await _client.Rest.GetChannelAsync(ticket.ThreadId) as IThreadChannel; }
                catch { /* gone */ }
            }

            var hours = (int)Math.Round(ageHours);

            if (doSecond)
            {
                ticket.Priority = SupportTicketPriority.Urgent;
                ticket.EscalationLevel = 2;

                var escalationRoleId = _config.TicketEscalationRoleId != 0
                    ? _config.TicketEscalationRoleId
                    : ticket.RoutedRoleId;

                if (thread is SocketThreadChannel st && _config.TicketEscalationRoleId != 0)
                    await AddRoleMembersToThreadAsync(st, guild, escalationRoleId);

                await db.SaveChangesAsync();
                _logger.LogInformation("Ticket #{Id} escalated to level 2 ({Hours}h unclaimed)", ticket.Id, hours);

                if (thread is IMessageChannel mc)
                    await SendPingAsync(mc, escalationRoleId,
                        $"⏫⏫ **Escalation** — ticket **#{ticket.Id}** has been open **{hours}h** without being claimed. "
                        + (escalationRoleId != 0 ? $"<@&{escalationRoleId}> please take this." : "Please take this."));
            }
            else
            {
                ticket.Priority = BumpPriority(ticket.Priority);
                ticket.EscalationLevel = 1;
                await db.SaveChangesAsync();
                _logger.LogInformation("Ticket #{Id} escalated to level 1 ({Hours}h unclaimed)", ticket.Id, hours);

                if (thread is IMessageChannel mc)
                    await SendPingAsync(mc, ticket.RoutedRoleId,
                        $"⏫ Ticket **#{ticket.Id}** has been open **{hours}h** without being claimed — priority bumped to **{ticket.Priority}**. "
                        + (ticket.RoutedRoleId != 0 ? $"<@&{ticket.RoutedRoleId}> please take a look." : "Please take a look."));
            }

            await _tickets.RefreshControlEmbedAsync(ticket, guild);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error escalating ticket #{Id}", ticketId);
        }
    }

    private static SupportTicketPriority BumpPriority(SupportTicketPriority p) =>
        p < SupportTicketPriority.Urgent ? p + 1 : SupportTicketPriority.Urgent;

    private static async Task SendPingAsync(IMessageChannel channel, ulong roleId, string content)
    {
        var mentions = roleId != 0
            ? new AllowedMentions { RoleIds = new List<ulong> { roleId } }
            : AllowedMentions.None;
        try { await channel.SendMessageAsync(content, allowedMentions: mentions); }
        catch { /* best-effort */ }
    }

    private async Task AddRoleMembersToThreadAsync(SocketThreadChannel thread, SocketGuild guild, ulong roleId)
    {
        if (roleId == 0) return;
        var role = guild.GetRole(roleId);
        if (role is null) return;

        var added = 0;
        foreach (var m in role.Members)
        {
            if (m.IsBot) continue;
            if (added >= 50) break;
            try { await thread.AddUserAsync(m); added++; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not add {User} to escalated thread", m.Id); }
        }
    }

    /// <summary>
    /// Un-archives a still-open ticket thread if Discord has auto-archived it.
    /// Active threads are in the socket cache; a cache miss means it's archived
    /// (or gone), so we REST-fetch and un-archive if needed.
    /// </summary>
    private async Task KeepAliveAsync(SocketGuild guild, SupportTicket ticket)
    {
        try
        {
            // Active (non-archived) threads are cached — nothing to do.
            if (guild.GetChannel(ticket.ThreadId) is SocketThreadChannel active && !active.IsArchived)
                return;

            if (await _client.Rest.GetChannelAsync(ticket.ThreadId) is IThreadChannel thread && thread.IsArchived)
            {
                await thread.ModifyAsync(p => p.Archived = false);
                _logger.LogDebug("Kept ticket #{Id} alive (un-archived thread)", ticket.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Keep-alive skipped for ticket #{Id}", ticket.Id);
        }
    }
}
