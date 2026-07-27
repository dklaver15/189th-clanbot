using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Disambiguate from Discord.Net's own Poll type under `using Discord;`.
using Poll = ClanGuardBot.Models.Poll;

namespace ClanGuardBot.Services;

/// <summary>
/// Background sweep that posts a single "still open — get your vote in" reminder
/// at the midpoint of each poll's life (<see cref="Poll.RemindAtUtc"/>): a 24h
/// poll reminds at hour 12, a 2h poll at hour 1. The reminder is a plain channel
/// message with no @mentions — it just re-surfaces the poll with a jump link and
/// a relative countdown to close.
///
/// The reminder lands in <see cref="BotConfig.PollAnnounceChannelId"/> (the clan's
/// general channel) rather than the poll's own channel — a nudge in the quiet
/// #polls channel reaches nobody who wasn't already looking. It falls back to the
/// poll's channel when cross-posting is off, unconfigured, opted out of for that
/// poll (<c>/poll cross_post:false</c>), or when the configured channel can't be
/// resolved.
///
/// Mirrors <see cref="PollCloseService"/>: a short interval, idempotent via the
/// <see cref="Poll.ReminderSent"/> flag so a sweep overlapping a restart can't
/// double-post. A poll closed (manually or on schedule) before its midpoint is
/// skipped, since the sweep only considers <see cref="PollStatus.Open"/> polls.
/// </summary>
public sealed class PollReminderService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<PollReminderService> _logger;

    public PollReminderService(
        DiscordSocketClient client,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<PollReminderService> logger)
    {
        _client   = client;
        _services = services;
        _config   = config.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Poll reminder sweep failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        List<int> due;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            due = await db.Polls
                .Where(p => p.Status == PollStatus.Open
                         && !p.ReminderSent
                         && p.RemindAtUtc != null
                         && p.RemindAtUtc <= now)
                .Select(p => p.Id)
                .ToListAsync(ct);
        }

        foreach (var pollId in due)
        {
            ct.ThrowIfCancellationRequested();
            await RemindAsync(pollId);
        }
    }

    private async Task RemindAsync(int pollId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll is null || poll.ReminderSent || poll.Status != PollStatus.Open) return;

        // Flip the flag first and persist, so even if the post below throws we
        // don't re-attempt forever (a missed reminder is harmless; a spammed one
        // is not).
        poll.ReminderSent = true;
        await db.SaveChangesAsync();

        try
        {
            var channel = ResolveReminderChannel(poll);
            if (channel is null) return;

            var close = new DateTimeOffset(poll.ClosesAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            var eb = new EmbedBuilder()
                .WithTitle("⏳ Poll still open")
                .WithColor(new Color(0xFEE75C))
                .WithDescription($"**{Trim(poll.Question, 250)}**");

            // Only worth naming the poll's channel when the reminder is somewhere
            // else — in the poll's own channel it's noise.
            if (channel.Id != poll.ChannelId)
                eb.AddField("Where", MentionUtils.MentionChannel(poll.ChannelId), inline: true);

            eb.AddField("Closes", $"<t:{close}:R>", inline: true)
              .AddField("​",
                  $"[Jump to the poll](https://discord.com/channels/{poll.GuildId}/{poll.ChannelId}/{poll.MessageId}) and get your vote in!");

            // No pings — explicitly suppress any mention parsing in the question text.
            await channel.SendMessageAsync(embed: eb.Build(), allowedMentions: AllowedMentions.None);

            _logger.LogInformation("Posted midpoint reminder for poll {PollId} '{Q}'", poll.Id, poll.Question);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post midpoint reminder for poll {PollId}", poll.Id);
        }
    }

    /// <summary>
    /// Where this poll's reminder goes: the configured cross-post channel when
    /// cross-posting applies to the poll, otherwise the poll's own channel. Null
    /// when neither resolves (the poll's channel was deleted, say) — the caller
    /// then simply skips the reminder.
    /// </summary>
    private IMessageChannel? ResolveReminderChannel(Poll poll)
    {
        var targetId = _config.PollAnnounceChannelId;
        var useAnnounceChannel = poll.CrossPost && _config.PollAnnounceEnabled && targetId != 0;

        if (useAnnounceChannel && _client.GetChannel(targetId) is IMessageChannel announce)
            return announce;

        if (useAnnounceChannel)
            _logger.LogWarning("Poll announce channel {ChannelId} not found — reminding in the poll's own channel instead",
                targetId);

        return _client.GetChannel(poll.ChannelId) as IMessageChannel;
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private static string Trim(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
