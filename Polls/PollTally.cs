using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Services;

/// <summary>
/// Aggregates persisted <see cref="PollVote"/> rows into per-option counts and
/// distinct-voter totals. Used both to re-render an anonymous poll on each vote
/// and to pick the winner at close. Identity is never returned — only counts —
/// so the same helper is safe for anonymous polls.
/// </summary>
public static class PollTally
{
    public sealed record Result(
        IReadOnlyDictionary<int, int> CountsByOptionId,
        int TotalVotes,
        int TotalVoters);

    public static async Task<Result> ComputeAsync(BotDbContext db, int pollId)
    {
        var votes = await db.PollVotes
            .Where(v => v.PollId == pollId)
            .Select(v => new { v.PollOptionId, v.UserId })
            .ToListAsync();

        var counts = votes
            .GroupBy(v => v.PollOptionId)
            .ToDictionary(g => g.Key, g => g.Count());

        var totalVoters = votes.Select(v => v.UserId).Distinct().Count();
        return new Result(counts, votes.Count, totalVoters);
    }

    /// <summary>
    /// The winning option(s) — all options tied at the max count (&gt; 0).
    /// Empty when nobody voted.
    /// </summary>
    public static List<PollOption> Winners(IReadOnlyList<PollOption> options, IReadOnlyDictionary<int, int> counts)
    {
        var max = options.Count == 0 ? 0 : options.Max(o => counts.GetValueOrDefault(o.Id));
        return max == 0
            ? new List<PollOption>()
            : options.Where(o => counts.GetValueOrDefault(o.Id) == max).OrderBy(o => o.Position).ToList();
    }
}
