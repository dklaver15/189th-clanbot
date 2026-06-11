using ClanGuardBot.Data;
using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Discord.Net's Discord.WebSocket.PollVote collides with our model under
// `using Discord.WebSocket;`. Pin the bare names to our EF models.
using Poll = ClanGuardBot.Models.Poll;
using PollVote = ClanGuardBot.Models.PollVote;

namespace ClanGuardBot.Handlers;

/// <summary>
/// Handles the buttons on ANONYMOUS poll posts: the per-option vote buttons
/// (<c>poll:vote:&lt;pollId&gt;:&lt;optionId&gt;</c>) and the creator/mod "Close
/// poll" button (<c>poll:close:&lt;pollId&gt;</c>). Native polls don't come here —
/// Discord owns their buttons (see <see cref="Services.PollGatewayVoteHandler"/>).
///
/// Each vote upserts a <see cref="PollVote"/> and re-renders the embed with the
/// new tallies — but the re-render only ever shows counts, never who voted, which
/// is the whole point of the anonymous path. A per-event striped lock serializes
/// the read-modify-render so two simultaneous voters can't clobber each other.
/// </summary>
public sealed class PollVoteInteractionHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PollVoteInteractionHandler> _logger;
    private readonly PollClosingService _closer;

    private const int LockStripeCount = 64;
    private readonly SemaphoreSlim[] _locks =
        Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private SemaphoreSlim GateFor(int pollId) => _locks[(uint)pollId % LockStripeCount];

    public PollVoteInteractionHandler(
        IServiceProvider services,
        ILogger<PollVoteInteractionHandler> logger,
        PollClosingService closer)
    {
        _services = services;
        _logger   = logger;
        _closer   = closer;
    }

    public void Register(DiscordSocketClient client)
    {
        client.ButtonExecuted += OnButtonExecutedAsync;
    }

    private async Task OnButtonExecutedAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        try
        {
            if (id.StartsWith(PollEmbedBuilder.VotePrefix, StringComparison.Ordinal))
                await HandleVoteAsync(component);
            else if (id.StartsWith(PollEmbedBuilder.ClosePrefix, StringComparison.Ordinal))
                await HandleCloseAsync(component);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Poll button {CustomId} failed", id);
            try { await component.DeferAsync(); } catch { /* already acked */ }
        }
    }

    // ─── Voting ────────────────────────────────────────────────────────────

    private async Task HandleVoteAsync(SocketMessageComponent component)
    {
        // poll:vote:<pollId>:<optionId>
        var parts = component.Data.CustomId.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[2], out var pollId) || !int.TryParse(parts[3], out var optionId))
        {
            await component.DeferAsync();
            return;
        }

        var gate = GateFor(pollId);
        await gate.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId);
            if (poll is null)
            {
                await component.RespondAsync("This poll no longer exists.", ephemeral: true);
                return;
            }
            if (poll.Status == PollStatus.Closed || DateTime.UtcNow >= poll.ClosesAtUtc)
            {
                await component.RespondAsync("This poll is closed — votes are no longer being counted.", ephemeral: true);
                return;
            }

            var options = await db.PollOptions.Where(o => o.PollId == pollId).OrderBy(o => o.Position).ToListAsync();
            var chosen  = options.FirstOrDefault(o => o.Id == optionId);
            if (chosen is null)
            {
                await component.DeferAsync();
                return;
            }

            var mine = await db.PollVotes.Where(v => v.PollId == pollId && v.UserId == component.User.Id).ToListAsync();
            var removed = false;

            if (poll.AllowMultiselect)
            {
                // Toggle just this option.
                var existing = mine.FirstOrDefault(v => v.PollOptionId == optionId);
                if (existing is not null)
                {
                    db.PollVotes.Remove(existing);
                    removed = true;
                }
                else
                {
                    db.PollVotes.Add(NewVote(pollId, optionId, component.User.Id));
                }
            }
            else
            {
                // Single choice: clicking your current pick clears it; otherwise it
                // replaces whatever you had.
                var hadThis = mine.Any(v => v.PollOptionId == optionId);
                db.PollVotes.RemoveRange(mine);
                if (hadThis)
                {
                    removed = true;
                }
                else
                {
                    db.PollVotes.Add(NewVote(pollId, optionId, component.User.Id));
                }
            }

            await db.SaveChangesAsync();

            var tally = await PollTally.ComputeAsync(db, pollId);
            await component.UpdateAsync(m =>
            {
                m.Embed      = PollEmbedBuilder.BuildEmbed(poll, options, tally.CountsByOptionId, tally.TotalVoters, PollImage.FileNameFor(poll));
                m.Components = PollEmbedBuilder.BuildComponents(poll, options, locked: false);
            });

            // Quiet, private confirmation — the only place a member sees their own
            // choice reflected back (the public embed shows totals only).
            var current = await db.PollVotes
                .Where(v => v.PollId == pollId && v.UserId == component.User.Id)
                .Select(v => v.PollOptionId)
                .ToListAsync();
            string confirm;
            if (current.Count == 0)
                confirm = removed ? "↩️ Your vote was removed." : "Your vote wasn't recorded — try again.";
            else
            {
                var picked = options.Where(o => current.Contains(o.Id)).Select(o => $"**{o.Label}**");
                confirm = $"✅ Recorded (anonymous): {string.Join(", ", picked)}";
            }
            await component.FollowupAsync(confirm, ephemeral: true);
        }
        finally
        {
            gate.Release();
        }
    }

    // ─── Closing ───────────────────────────────────────────────────────────

    private async Task HandleCloseAsync(SocketMessageComponent component)
    {
        // poll:close:<pollId>
        var parts = component.Data.CustomId.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[2], out var pollId))
        {
            await component.DeferAsync();
            return;
        }

        bool alreadyClosed;
        ulong creatorId;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var poll = await db.Polls.FirstOrDefaultAsync(p => p.Id == pollId);
            if (poll is null)
            {
                await component.RespondAsync("This poll no longer exists.", ephemeral: true);
                return;
            }
            creatorId     = poll.CreatorId;
            alreadyClosed = poll.Status == PollStatus.Closed;
        }

        var isCreator = component.User.Id == creatorId;
        var isMod = component.User is SocketGuildUser gu &&
                    (gu.GuildPermissions.ManageMessages || gu.GuildPermissions.Administrator);
        if (!isCreator && !isMod)
        {
            await component.RespondAsync("Only the poll's creator or a moderator can close it.", ephemeral: true);
            return;
        }

        if (alreadyClosed)
        {
            await component.RespondAsync("This poll is already closed.", ephemeral: true);
            return;
        }

        // Ack the interaction, then let the shared closer lock the post + announce.
        await component.DeferAsync();
        await _closer.CloseAsync(pollId, component.User.Id);
        await component.FollowupAsync("🛑 Poll closed.", ephemeral: true);
    }

    private static PollVote NewVote(int pollId, int optionId, ulong userId) => new()
    {
        PollId       = pollId,
        PollOptionId = optionId,
        UserId       = userId,
        CreatedAt    = DateTime.UtcNow,
    };
}
