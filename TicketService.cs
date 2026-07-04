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
/// Shared ticket logic used by both TicketInteractionHandler (button/modal
/// flow) and TicketMaintenanceService (background sweep): the ticket embed
/// builder, the close pipeline (DB update → transcript → log channel → embed
/// flip → archive + lock), and the transcript renderer. Keeping this in one
/// place means a ticket closed by an officer's button and one auto-closed by
/// the sweep produce identical results.
/// </summary>
public sealed class TicketService
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<TicketService> _logger;
    private readonly BotConfig _config;

    public TicketService(
        IServiceProvider services,
        DiscordSocketClient client,
        ILogger<TicketService> logger,
        IOptions<BotConfig> config)
    {
        _services = services;
        _client   = client;
        _logger   = logger;
        _config   = config.Value;
    }

    // ─── Close pipeline ───────────────────────────────────────────────

    /// <summary>
    /// Closes a ticket: stamps the DB row, posts the transcript to the log
    /// channel, flips the control embed to closed and drops its buttons, posts
    /// a closing note, and archives + locks the thread. Idempotent — returns
    /// false if the ticket is already closed or missing. Works whether the
    /// thread is active or already archived (fetches via REST if the socket
    /// cache misses).
    /// </summary>
    public async Task<bool> CloseTicketAsync(
        int ticketId, SocketGuild guild, ulong closedByUserId, string closedByName, string? resolution)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var ticket = await db.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        if (ticket is null || ticket.Status == SupportTicketStatus.Closed) return false;

        var now = DateTime.UtcNow;
        ticket.Status           = SupportTicketStatus.Closed;
        ticket.ClosedUtc        = now;
        ticket.ClosedByUserId   = closedByUserId;
        ticket.ClosedByUsername = closedByName;
        ticket.Resolution       = string.IsNullOrWhiteSpace(resolution) ? null : resolution;
        ticket.LastActivityUtc  = now;
        db.SupportTicketMessages.Add(new SupportTicketMessage
        {
            TicketId          = ticket.Id,
            AuthorUserId      = closedByUserId,
            AuthorDisplayName = closedByName,
            Content           = $"Closed by {closedByName}",
            SentUtc           = now,
            Direction         = SupportTicketMessageDirection.System,
        });
        await db.SaveChangesAsync();

        var messages = await db.SupportTicketMessages
            .Where(m => m.TicketId == ticket.Id)
            .OrderBy(m => m.SentUtc)
            .ToListAsync();

        await PostTranscriptAsync(guild, ticket, messages);

        // Resolve the thread (active from socket cache, else REST for archived).
        IThreadChannel? thread = guild.GetChannel(ticket.ThreadId) as IThreadChannel;
        if (thread is null)
        {
            try { thread = await _client.Rest.GetChannelAsync(ticket.ThreadId) as IThreadChannel; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not REST-fetch thread for ticket #{Id}", ticket.Id); }
        }

        if (thread is IMessageChannel msgChannel)
        {
            try
            {
                if (await msgChannel.GetMessageAsync(ticket.ControlMessageId) is IUserMessage control)
                {
                    var category = _config.GetTicketCategories().FirstOrDefault(c => c.Key == ticket.CategoryKey);
                    await control.ModifyAsync(p =>
                    {
                        p.Embed      = BuildTicketEmbed(ticket, category);
                        p.Components = new ComponentBuilder().Build();
                    });
                }

                await msgChannel.SendMessageAsync(
                    $"🔒 Ticket closed by <@{closedByUserId}>."
                    + (ticket.Resolution is { } r ? $"\n**Resolution:** {r}" : ""),
                    allowedMentions: AllowedMentions.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not post close notice for ticket #{Id}", ticket.Id);
            }
        }

        if (thread is not null)
        {
            try { await thread.ModifyAsync(p => { p.Archived = true; p.Locked = true; }); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not archive/lock thread for ticket #{Id}", ticket.Id); }
        }

        _logger.LogInformation("Ticket #{Id} closed by {Closer}", ticket.Id, closedByName);
        return true;
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
            .AddField("Priority", PriorityLabel(ticket.Priority), inline: true)
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

    // ─── Embed ────────────────────────────────────────────────────────

    public Embed BuildTicketEmbed(SupportTicket ticket, TicketCategoryDef? category)
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
            .AddField("Priority", PriorityLabel(ticket.Priority), inline: true)
            .AddField("Status", statusLabel, inline: true)
            .WithFooter($"Opened {ticket.CreatedUtc:yyyy-MM-dd HH:mm} UTC");

        if (ticket.ClaimedByUsername is { } claimant && ticket.Status != SupportTicketStatus.Closed)
            builder.AddField("Claimed by", claimant, inline: true);

        if (!string.IsNullOrWhiteSpace(_config.TicketPanelThumbnailUrl))
            builder.WithThumbnailUrl(_config.TicketPanelThumbnailUrl);

        return builder.Build();
    }

    // ─── Transcript / labels ──────────────────────────────────────────

    private static string BuildTranscript(SupportTicket ticket, List<SupportTicketMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"189th Support Ticket #{ticket.Id} — {ticket.CategoryLabel}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine("Status:     Closed");
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

    private static Color PriorityColor(SupportTicketPriority p) => p switch
    {
        SupportTicketPriority.Low    => new Color(0x99, 0xAA, 0xB5),
        SupportTicketPriority.Normal => new Color(0x58, 0x65, 0xF2),
        SupportTicketPriority.High   => new Color(0xE6, 0x7E, 0x22),
        SupportTicketPriority.Urgent => new Color(0xE7, 0x4C, 0x3C),
        _                            => new Color(0x58, 0x65, 0xF2),
    };

    public static string PriorityLabel(SupportTicketPriority p) => p switch
    {
        SupportTicketPriority.Low    => "⚪ Low",
        SupportTicketPriority.Normal => "🔵 Normal",
        SupportTicketPriority.High   => "🟠 High",
        SupportTicketPriority.Urgent => "🔴 Urgent",
        _                            => p.ToString(),
    };

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
