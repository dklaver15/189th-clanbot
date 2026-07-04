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
        if (_config.TicketAutoCloseInactivityDays <= 0)
        {
            _logger.LogInformation(
                "Ticket maintenance disabled (TicketAutoCloseInactivityDays=0).");
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.TicketMaintenanceIntervalMinutes));
        _logger.LogInformation(
            "Ticket maintenance started. Interval={Interval}m, inactivity window={Days}d.",
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
        var cutoffDays = _config.TicketAutoCloseInactivityDays;
        var now = DateTime.UtcNow;

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
