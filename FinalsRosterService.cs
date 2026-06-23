using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// Shared presentation helpers for THE FINALS surfaces (command, board, announcements).
/// </summary>
public static class FinalsFormat
{
    /// <summary>An emoji for a league tier, matched on its name prefix.</summary>
    public static string LeagueEmoji(string? league)
    {
        if (string.IsNullOrWhiteSpace(league)) return "🎮";
        var l = league.Trim().ToLowerInvariant();
        if (l.StartsWith("ruby")) return "❤️";
        if (l.StartsWith("diamond")) return "💎";
        if (l.StartsWith("platinum")) return "🔷";
        if (l.StartsWith("gold")) return "🥇";
        if (l.StartsWith("silver")) return "🥈";
        if (l.StartsWith("bronze")) return "🥉";
        return "🎮";
    }

    /// <summary>A ▲/▼/▬ marker for a 24h position change (positive = climbed).</summary>
    public static string ChangeMarker(int change)
    {
        if (change > 0) return $"▲ {change:N0}";
        if (change < 0) return $"▼ {Math.Abs(change):N0}";
        return "▬ 0";
    }

    /// <summary>The platforms a player has linked, e.g. "Steam, PSN".</summary>
    public static string Platforms(FinalsEntry e)
    {
        var p = new List<string>();
        if (!string.IsNullOrWhiteSpace(e.SteamName)) p.Add("Steam");
        if (!string.IsNullOrWhiteSpace(e.PsnName)) p.Add("PSN");
        if (!string.IsNullOrWhiteSpace(e.XboxName)) p.Add("Xbox");
        return p.Count > 0 ? string.Join(", ", p) : "—";
    }
}

/// <summary>
/// A clan member resolved to their THE FINALS leaderboard standing.
/// </summary>
public sealed record FinalsMemberRank(
    ulong? DiscordId,
    string DiscordName,
    string MatchedVia,
    FinalsEntry Entry);

/// <summary>
/// Joins the gamertag roster (Google Sheets) to the cached THE FINALS leaderboard
/// (<see cref="FinalsApiService"/>) and returns the clan members who are currently
/// ranked (i.e. in the global top 10k), best first. Shared by the leaderboard board
/// and the rank-up announcer so a single leaderboard fetch + single sheet read
/// serves both.
/// </summary>
public sealed class FinalsRosterService
{
    private readonly GoogleSheetsService _sheets;
    private readonly FinalsApiService _api;
    private readonly ILogger<FinalsRosterService> _logger;

    public FinalsRosterService(
        GoogleSheetsService sheets,
        FinalsApiService api,
        ILogger<FinalsRosterService> logger)
    {
        _sheets = sheets;
        _api = api;
        _logger = logger;
    }

    /// <summary>
    /// Reads every roster handle and matches it against the leaderboard. Ensures the
    /// leaderboard cache is warm first (awaits an in-flight refresh), so callers get
    /// real data on the first cycle. Returns ranked members sorted by rank score
    /// (highest first). Members with no leaderboard presence are omitted.
    /// </summary>
    public async Task<List<FinalsMemberRank>> GetClanRankingsAsync(CancellationToken ct = default)
    {
        await _api.EnsureWarmAsync(ct);

        List<GamertagRosterEntry> roster;
        try
        {
            roster = await _sheets.GetAllGamertagsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FINALS rankings: could not read the gamertag roster");
            return new List<FinalsMemberRank>();
        }

        var ranked = new List<FinalsMemberRank>();
        foreach (var member in roster)
        {
            var match = _api.Match(member.Tags);
            if (match is null) continue;

            ranked.Add(new FinalsMemberRank(
                member.DiscordId,
                string.IsNullOrWhiteSpace(member.Tags.DiscordName) ? match.Entry.Name : member.Tags.DiscordName,
                match.MatchedVia,
                match.Entry));
        }

        // Highest rank score first. Dedupe on the leaderboard entry in case two
        // roster rows resolve to the same player (e.g. duplicate handles).
        return ranked
            .GroupBy(r => r.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.Entry.RankScore)
            .ToList();
    }
}
