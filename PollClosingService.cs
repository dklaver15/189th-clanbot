using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Disambiguate from Discord.Net's own Poll type under `using Discord;`.
using Poll = ClanGuardBot.Models.Poll;

namespace ClanGuardBot.Services;

/// <summary>
/// Closes a poll and posts the winner announcement. Shared by the background
/// sweep (<see cref="PollCloseService"/>, which closes polls at their scheduled
/// time) and the "Close poll" button on anonymous posts — so the close + announce
/// behaviour lives in exactly one place and is idempotent.
///
/// • Anonymous polls: the embed is re-rendered in its closed state and the vote
///   buttons are disabled.
/// • Native polls: Discord finalizes its own poll UI at the duration we set, so
///   we only post the announcement (computed from the votes we captured live via
///   the gateway events).
/// </summary>
public sealed class PollClosingService
{
    private readonly DiscordSocketClient _client;
    private readonly IServiceProvider _services;
    private readonly ILogger<PollClosingService> _logger;

    private const int LockStripeCount = 64;
    private readonly SemaphoreSlim[] _locks =
        Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private SemaphoreSlim GateFor(int pollId) => _locks[(uint)pollId % LockStripeCount];

    public PollClosingService(
        DiscordSocketClient client,
        IServiceProvider services,
        ILogger<PollClosingService> logger)
    {
        _client   = client;
        _services = services;
        _logger   = logger;
    }

    /// <summary>
    /// Idempotently closes the poll (no-op if already closed) and announces the
    /// winner. <paramref name="closedByUserId"/> is the member who pressed Close,
    /// or null for the scheduled auto-close.
    /// </summary>
    public async Task CloseAsync(int pollId, ulong? closedByUserId)
    {
        var gate = GateFor(pollId);
        await gate.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId);
            if (poll is null) return;

            var options = await db.PollOptions.Where(o => o.PollId == pollId).OrderBy(o => o.Position).ToListAsync();
            var tally   = await PollTally.ComputeAsync(db, pollId);

            // Flip to Closed once (the sweep may race a manual close / restart).
            if (poll.Status != PollStatus.Closed)
            {
                poll.Status      = PollStatus.Closed;
                poll.ClosedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();

                if (poll.Kind == PollKind.Anonymous)
                    await LockAnonymousPostAsync(poll, options, tally);
            }

            // Announce exactly once.
            if (poll.AnnounceOnClose && !poll.ResultsAnnounced)
            {
                var announceId = await PostAnnouncementAsync(poll, options, tally);
                if (announceId is not null)
                {
                    poll.ResultsMessageId = announceId;
                    poll.ResultsAnnounced = true;
                    await db.SaveChangesAsync();
                }
            }

            _logger.LogInformation("Closed poll {PollId} '{Q}' ({Kind}); {Votes} votes from {Voters} voters{By}",
                poll.Id, poll.Question, poll.Kind, tally.TotalVotes, tally.TotalVoters,
                closedByUserId is ulong u ? $" (closed by {u})" : " (auto)");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task LockAnonymousPostAsync(Poll poll, List<PollOption> options, PollTally.Result tally)
    {
        try
        {
            if (_client.GetChannel(poll.ChannelId) is not IMessageChannel channel) return;
            if (await channel.GetMessageAsync(poll.MessageId) is not IUserMessage msg) return;

            var embed = PollEmbedBuilder.BuildEmbed(
                poll, options, tally.CountsByOptionId, tally.TotalVoters, PollImage.FileNameFor(poll));
            await msg.ModifyAsync(m =>
            {
                m.Embed      = embed;
                m.Components = PollEmbedBuilder.BuildComponents(poll, options, locked: true);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lock anonymous poll {PollId} post on close", poll.Id);
        }
    }

    private async Task<ulong?> PostAnnouncementAsync(Poll poll, List<PollOption> options, PollTally.Result tally)
    {
        try
        {
            if (_client.GetChannel(poll.ChannelId) is not IMessageChannel channel) return null;

            var winners = PollTally.Winners(options, tally.CountsByOptionId);

            var eb = new EmbedBuilder()
                .WithTitle("📊 Poll closed")
                .WithColor(new Color(0x57F287))
                .WithDescription($"**{Trim(poll.Question, 250)}**");

            if (winners.Count == 0)
            {
                eb.AddField("Result", "No votes were cast.");
            }
            else
            {
                var max = tally.CountsByOptionId.GetValueOrDefault(winners[0].Id);
                var pct = tally.TotalVotes == 0 ? 0 : (int)Math.Round(max * 100.0 / tally.TotalVotes);
                var label = winners.Count == 1
                    ? $"{Emoji(winners[0])}**{Trim(winners[0].Label, 80)}**"
                    : "Tie — " + string.Join(", ", winners.Select(w => $"{Emoji(w)}**{Trim(w.Label, 60)}**"));
                eb.AddField(winners.Count == 1 ? "🏆 Winner" : "🏆 Winners",
                    $"{label} · {max} {(max == 1 ? "vote" : "votes")} ({pct}%)");
            }

            // Full breakdown, highest first, each row led by a bar scaled to the
            // front-runner (see PollEmbedBuilder.Bar for why it isn't scaled by
            // percentage). Bar first, not last: it's fixed width, so putting it at
            // the start of the line is what makes every bar line up under the one
            // above regardless of how long the labels are. Counts only, never
            // identities, so this stays safe for anonymous polls.
            var ranked = options
                .OrderByDescending(o => tally.CountsByOptionId.GetValueOrDefault(o.Id))
                .ThenBy(o => o.Position)
                .ToList();

            var lead = ranked.Count == 0 ? 0 : tally.CountsByOptionId.GetValueOrDefault(ranked[0].Id);

            // Discord rejects an embed field whose value exceeds 1024 characters,
            // and a rejected field kills the whole announcement. Ten options with
            // long labels have to degrade to a truncated list, not to no post at
            // all. Reserve room for the "…and N more" tail before filling.
            const int FieldLimit  = 1024;
            const int TailReserve = 24;

            var body  = new StringBuilder();
            var shown = 0;

            foreach (var o in ranked)
            {
                var c = tally.CountsByOptionId.GetValueOrDefault(o.Id);
                var p = tally.TotalVotes == 0 ? 0 : (int)Math.Round(c * 100.0 / tally.TotalVotes);
                var line = $"`{PollEmbedBuilder.Bar(c, lead)}` {Emoji(o)}{Trim(o.Label, 56)} · **{c}** ({p}%)\n";

                if (body.Length + line.Length > FieldLimit - TailReserve) break;

                body.Append(line);
                shown++;
            }

            if (shown < ranked.Count) body.Append($"…and {ranked.Count - shown} more");

            eb.AddField("Results", body.Length == 0 ? "*(no options)*" : body.ToString().TrimEnd());

            eb.AddField("​",
                $"{tally.TotalVoters} {(tally.TotalVoters == 1 ? "voter" : "voters")} · " +
                $"[Jump to poll](https://discord.com/channels/{poll.GuildId}/{poll.ChannelId}/{poll.MessageId})");

            if (poll.Kind == PollKind.Anonymous)
                eb.WithFooter("🔒 Anonymous poll — individual votes were never shown.");

            var sent = await channel.SendMessageAsync(embed: eb.Build());
            return sent.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to post close announcement for poll {PollId}", poll.Id);
            return null;
        }
    }

    private static string Emoji(PollOption o) => string.IsNullOrWhiteSpace(o.Emoji) ? "" : o.Emoji + " ";

    private static string Trim(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
