using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Captures votes on NATIVE Discord polls from the gateway
/// (<c>PollVoteAdded</c> / <c>PollVoteRemoved</c>, which need the
/// <c>GuildMessagePolls</c> intent) and persists them as <see cref="PollVote"/>
/// rows. Discord owns the native poll UI and discards its per-voter breakdown
/// once the message ages out — this is what makes the data survive for the
/// officer-briefing analytics. Anonymous polls never reach here; their votes come
/// through our own buttons (<c>PollVoteInteractionHandler</c>).
///
/// The event reports the message id, the voter, and the 1-based answer id; we map
/// those to our persisted <see cref="Poll"/> (by message id) and
/// <see cref="PollOption"/> (by <see cref="PollOption.AnswerId"/>). Events for
/// polls we didn't create simply find no row and are ignored.
/// </summary>
public sealed class PollGatewayVoteHandler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PollGatewayVoteHandler> _logger;

    private const int LockStripeCount = 64;
    private readonly SemaphoreSlim[] _locks =
        Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private SemaphoreSlim GateFor(ulong messageId) => _locks[messageId % LockStripeCount];

    public PollGatewayVoteHandler(IServiceProvider services, ILogger<PollGatewayVoteHandler> logger)
    {
        _services = services;
        _logger   = logger;
    }

    public void Register(DiscordSocketClient client)
    {
        // Inferred lambda params keep this resilient to the exact Cacheable<>
        // generic spelling — we only need the message id, the voter id, and the
        // answer id. (Args: user, channel, message, guild, answerId.)
        client.PollVoteAdded   += (user, _, message, _, answerId) => HandleAsync(message.Id, user.Id, answerId, added: true);
        client.PollVoteRemoved += (user, _, message, _, answerId) => HandleAsync(message.Id, user.Id, answerId, added: false);
    }

    private async Task HandleAsync(ulong messageId, ulong userId, ulong answerId, bool added)
    {
        var gate = GateFor(messageId);
        await gate.WaitAsync();
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var poll = await db.Polls.FirstOrDefaultAsync(p => p.MessageId == messageId && p.Kind == PollKind.Native);
            if (poll is null) return;                 // not one of our native polls
            if (poll.Status != PollStatus.Open) return; // ignore late events after close

            var answer = unchecked((int)answerId);
            var option = await db.PollOptions.FirstOrDefaultAsync(o => o.PollId == poll.Id && o.AnswerId == answer);
            if (option is null) return;

            var existing = await db.PollVotes
                .FirstOrDefaultAsync(v => v.PollId == poll.Id && v.UserId == userId && v.PollOptionId == option.Id);

            if (added)
            {
                if (existing is null)
                {
                    db.PollVotes.Add(new PollVote
                    {
                        PollId       = poll.Id,
                        PollOptionId = option.Id,
                        UserId       = userId,
                        CreatedAt    = DateTime.UtcNow,
                    });
                    await db.SaveChangesAsync();
                }
            }
            else if (existing is not null)
            {
                db.PollVotes.Remove(existing);
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record native poll vote (msg {MessageId}, answer {AnswerId})", messageId, answerId);
        }
        finally
        {
            gate.Release();
        }
    }
}
