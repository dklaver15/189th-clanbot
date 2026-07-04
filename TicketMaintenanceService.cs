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

        // ── 1. End expired leaves (independent of the auto-close setting) ──
        List<int> endedLeaveIds;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            endedLeaveIds = await db.SupportTickets
                .Where(t => t.ReserveAssigned && t.LeaveEndUtc != null && t.LeaveEndUtc <= now)
                .Select(t => t.Id)
                .ToListAsync(ct);
        }
        foreach (var id in endedLeaveIds)
        {
            if (ct.IsCancellationRequested) break;
            await EndLeaveAsync(id);
        }

        // ── 2. Keep-alive + auto-close (only when enabled) ──
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
    /// Ends an approved leave whose window has passed: removes the Reserve role,
    /// resets the member's AWOL activity window (so the leave period can't
    /// instantly re-flag them — same mechanism RankTrackingHandler uses on AWOL
    /// removal), clears ReserveAssigned, and DMs the member. Clears the flag
    /// even if the member/role is gone so it isn't retried forever.
    /// </summary>
    private async Task EndLeaveAsync(int ticketId)
    {
        var guild = default(SocketGuild);
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
            if (ticket is null || !ticket.ReserveAssigned) return;

            guild = _client.GetGuild(ticket.GuildId);
            if (guild is null) return; // bot not ready for this guild; retry next cycle

            var member = guild.GetUser(ticket.OpenerUserId);
            var reserveRole = guild.Roles.FirstOrDefault(r =>
                r.Name.Equals(_config.ReserveRoleName, StringComparison.OrdinalIgnoreCase));

            if (member is not null && reserveRole is not null && member.Roles.Any(r => r.Id == reserveRole.Id))
            {
                try { await member.RemoveRoleAsync(reserveRole); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove Reserve for ended leave (ticket #{Id})", ticketId); }
            }

            // Fresh activity window so the leave gap doesn't immediately flag them.
            if (member is not null)
            {
                var activity = await db.UserActivities
                    .FirstOrDefaultAsync(a => a.GuildId == guild.Id && a.UserId == member.Id);
                if (activity is null)
                {
                    activity = new UserActivity { GuildId = guild.Id, UserId = member.Id, Username = member.ToString() ?? member.Username };
                    db.UserActivities.Add(activity);
                }
                activity.WindowResetAt = DateTime.UtcNow;
            }

            ticket.ReserveAssigned = false;
            await db.SaveChangesAsync();

            _logger.LogInformation("Leave ended for ticket #{Id}; Reserve removed, window reset", ticketId);

            if (member is not null)
            {
                try
                {
                    var dm = await member.CreateDMChannelAsync();
                    await dm.SendMessageAsync(
                        $"👋 Welcome back! Your time-off (ticket #{ticketId}) has ended and your "
                        + $"**{_config.ReserveRoleName}** status has been removed. You've got a fresh "
                        + "activity window, so no worries about being flagged.",
                        allowedMentions: AllowedMentions.None);
                }
                catch (Exception ex) { _logger.LogInformation(ex, "Could not DM leave-end notice for ticket #{Id}", ticketId); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ending leave for ticket #{Id}", ticketId);
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
