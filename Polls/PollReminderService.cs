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
///
/// ── Near-duplicate grouping ──
/// The same question often gets posted twice: once in a game channel, once in
/// #polls to reach everyone. Both are legitimate polls, but two near-identical
/// "still open" embeds minutes apart in the general channel read as the bot
/// double-posting. So when a reminder comes due, any OTHER open poll that
/// <see cref="PollSimilarity"/> judges to be the same ask, reminds into the same
/// channel, and is due within <see cref="GroupWindow"/> is folded into ONE
/// reminder listing every poll with its own jump link. The whole group is marked
/// <see cref="Poll.ReminderSent"/> together, so the companions don't nudge again
/// on their own. Their reminder just arrives a little early, inside this one.
/// </summary>
public sealed class PollReminderService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How far ahead to look for a near-duplicate poll to fold into this reminder.
    /// Bounded rather than "any open duplicate" so a long-running poll can't have
    /// its own midpoint nudge silently swallowed hours early by a copy posted
    /// today; the reask that causes this is minutes old, not days.
    /// </summary>
    private static readonly TimeSpan GroupWindow = TimeSpan.FromHours(2);

    /// <summary>Cap on polls listed in one grouped reminder, so the embed stays readable.</summary>
    private const int MaxGrouped = 4;

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

        var channel = ResolveReminderChannel(poll);

        // Anything else open that's asking the same thing into the same channel
        // rides along in this one reminder instead of posting its own.
        var group = new List<Poll> { poll };
        if (channel is not null)
            group.AddRange(await FindNearDuplicatesAsync(db, poll, channel.Id));

        // Flip the flags first and persist, so even if the post below throws we
        // don't re-attempt forever (a missed reminder is harmless; a spammed one
        // is not). Companions are flipped too, since they're named in the message
        // we're about to send, so their own nudge would be the duplicate.
        foreach (var p in group) p.ReminderSent = true;
        await db.SaveChangesAsync();

        if (channel is null) return;

        try
        {
            // No pings — explicitly suppress any mention parsing in the question text.
            await channel.SendMessageAsync(embed: BuildReminderEmbed(group, channel.Id),
                allowedMentions: AllowedMentions.None);

            if (group.Count == 1)
                _logger.LogInformation("Posted midpoint reminder for poll {PollId} '{Q}'", poll.Id, poll.Question);
            else
                _logger.LogInformation("Posted grouped midpoint reminder for polls {PollIds} (near-duplicate questions)",
                    string.Join(", ", group.Select(p => p.Id)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post midpoint reminder for poll {PollId}", poll.Id);
        }
    }

    /// <summary>
    /// Other open polls that are the same ask as <paramref name="poll"/>, remind
    /// into the same channel, and come due soon enough to be worth folding in
    /// (<see cref="GroupWindow"/>). Capped at <see cref="MaxGrouped"/> total.
    ///
    /// The same-channel condition matters: a poll that opted out of cross-posting
    /// reminds in its own channel, where there's no duplicate to avoid. Folding
    /// it in here would move its reminder somewhere its creator deliberately
    /// kept it out of.
    /// </summary>
    private async Task<List<Poll>> FindNearDuplicatesAsync(BotDbContext db, Poll poll, ulong channelId)
    {
        var cutoff = DateTime.UtcNow + GroupWindow;

        var candidates = await db.Polls
            .Where(p => p.Id != poll.Id
                     && p.GuildId == poll.GuildId
                     && p.Status == PollStatus.Open
                     && !p.ReminderSent
                     && p.RemindAtUtc != null
                     && p.RemindAtUtc <= cutoff)
            .OrderBy(p => p.RemindAtUtc)
            .ToListAsync();

        var dupes = new List<Poll>();
        foreach (var candidate in candidates)
        {
            if (dupes.Count >= MaxGrouped - 1) break;
            if (ResolveReminderChannel(candidate)?.Id != channelId) continue;
            if (!PollSimilarity.AreNearDuplicates(poll.Question, candidate.Question)) continue;

            dupes.Add(candidate);
            _logger.LogInformation(
                "Folding poll {OtherId} '{OtherQ}' into poll {PollId}'s reminder, near-duplicate question (score {Score:0.00})",
                candidate.Id, candidate.Question, poll.Id, PollSimilarity.Score(poll.Question, candidate.Question));
        }

        return dupes;
    }

    /// <summary>
    /// One poll → the original single-poll embed. Several → one embed listing each
    /// with its own channel, countdown and jump link, since the questions differ
    /// in wording even when they're the same ask.
    /// </summary>
    private static Embed BuildReminderEmbed(IReadOnlyList<Poll> polls, ulong channelId)
    {
        var eb = new EmbedBuilder().WithColor(new Color(0xFEE75C));

        if (polls.Count == 1)
        {
            var poll = polls[0];
            eb.WithTitle("⏳ Poll still open")
              .WithDescription($"**{Trim(poll.Question, 250)}**");

            // Only worth naming the poll's channel when the reminder is somewhere
            // else — in the poll's own channel it's noise.
            if (channelId != poll.ChannelId)
                eb.AddField("Where", MentionUtils.MentionChannel(poll.ChannelId), inline: true);

            eb.AddField("Closes", $"<t:{Unix(poll.ClosesAtUtc)}:R>", inline: true)
              .AddField("​", $"[Jump to the poll]({JumpLink(poll)}) and get your vote in!");

            return eb.Build();
        }

        eb.WithTitle($"⏳ {polls.Count} polls still open")
          .WithDescription("These are all asking much the same thing. Get your vote into whichever you like:");

        foreach (var poll in polls)
        {
            var where = poll.ChannelId == channelId
                ? string.Empty
                : $"{MentionUtils.MentionChannel(poll.ChannelId)} · ";

            eb.AddField(Trim(poll.Question, 250),
                $"{where}closes <t:{Unix(poll.ClosesAtUtc)}:R> · [Jump to the poll]({JumpLink(poll)})");
        }

        return eb.Build();
    }

    private static long Unix(DateTime utc) =>
        new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string JumpLink(Poll poll) =>
        $"https://discord.com/channels/{poll.GuildId}/{poll.ChannelId}/{poll.MessageId}";

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
