using System.Text;
using ClanGuardBot.Models;
using Discord;

namespace ClanGuardBot.Services;

/// <summary>
/// Shared presentation and ordering for every Sleeper surface (the slash commands
/// and the weekly matchup posts).
///
/// Standings order and game pairing live here rather than in each caller, so the
/// tiebreak rule and the "which two rosters are playing each other" logic have one
/// definition. A command and a scheduled post disagreeing about who is in first
/// place would be worse than either being wrong.
/// </summary>
public static class SleeperFormat
{
    /// <summary>Sleeper's brand green, used on every embed so the surfaces read as one feature.</summary>
    public static readonly Color BrandColor = new(0x00CEB8);

    private const int MaxDescription = 3900; // headroom under Discord's 4096 cap

    // ─── Ordering ────────────────────────────────────────────────────────────

    /// <summary>
    /// Standings order: wins, then fewest losses, then points for.
    ///
    /// Points for as the tiebreak matches what Sleeper itself shows by default and
    /// is the usual league convention. It is not read from the league's configured
    /// tiebreaker, because Sleeper does not expose that setting, so this is stated
    /// on the embed footer rather than presented as the league's official rule.
    /// </summary>
    public static IReadOnlyList<SleeperTeam> SortStandings(IEnumerable<SleeperTeam> teams) =>
        teams
            .OrderByDescending(t => t.Wins)
            .ThenBy(t => t.Losses)
            .ThenByDescending(t => t.PointsFor)
            .ThenBy(t => t.RosterId)
            .ToList();

    /// <summary>
    /// Pairs one week's flat matchup rows into games.
    ///
    /// Sleeper returns one row per roster and links the two sides through a shared
    /// matchup_id. Rows with no matchup_id (a bye) and any group that does not have
    /// exactly two sides are dropped rather than guessed at: a half-rendered game
    /// is more confusing than an absent one.
    /// </summary>
    public static IReadOnlyList<SleeperGame> PairGames(
        IEnumerable<SleeperMatchupSide> sides,
        IReadOnlyList<SleeperTeam> teams)
    {
        var byRoster = teams.ToDictionary(t => t.RosterId);
        var games = new List<SleeperGame>();

        foreach (var group in sides.Where(s => s.MatchupId is not null).GroupBy(s => s.MatchupId!.Value))
        {
            var pair = group.OrderBy(s => s.RosterId).ToList();
            if (pair.Count != 2) continue;
            if (!byRoster.TryGetValue(pair[0].RosterId, out var home)) continue;
            if (!byRoster.TryGetValue(pair[1].RosterId, out var away)) continue;

            games.Add(new SleeperGame(group.Key, home, pair[0], away, pair[1]));
        }

        // Closest game first once scores are live, so the tightest matchup is at the
        // top of the scoreboard instead of buried by matchup id.
        return games.Any(g => g.HasStarted)
            ? games.OrderBy(g => g.Margin).ToList()
            : games.OrderBy(g => g.MatchupId).ToList();
    }

    // ─── Names ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How a team is written in a post: an @mention when the owner has linked their
    /// Discord account and mentions are on, otherwise the plain team name.
    ///
    /// The mention is appended to the team name rather than replacing it, because
    /// the team name is what people recognise in a fantasy channel and a bare
    /// mention would strip the league's flavour out of every line.
    /// </summary>
    public static string TeamLine(
        SleeperTeam team,
        IReadOnlyDictionary<string, ulong>? links,
        bool allowMentions)
    {
        var label = Escape(team.Label);

        if (!allowMentions || links is null || string.IsNullOrWhiteSpace(team.OwnerId)) return label;
        if (!links.TryGetValue(team.OwnerId!, out var discordId)) return label;

        return $"{label} (<@{discordId}>)";
    }

    /// <summary>
    /// Escapes Discord markdown and neutralises stray @ so a team name cannot
    /// break the embed or ping the server. Sleeper team names are user-supplied
    /// free text, so this runs on every one of them.
    /// </summary>
    public static string Escape(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(unnamed)";
        return s.Replace("\\", "\\\\")
                .Replace("*", "\\*")
                .Replace("_", "\\_")
                .Replace("~", "\\~")
                .Replace("`", "\\`")
                .Replace("|", "\\|")
                .Replace("@", "@​")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim();
    }

    /// <summary>Sleeper's CDN path for a user or league avatar. Null when there is no avatar set.</summary>
    public static string? AvatarUrl(string? avatarId, bool thumbnail = false) =>
        string.IsNullOrWhiteSpace(avatarId)
            ? null
            : thumbnail
                ? $"https://sleepercdn.com/avatars/thumbs/{avatarId}"
                : $"https://sleepercdn.com/avatars/{avatarId}";

    // ─── Standings ───────────────────────────────────────────────────────────

    /// <summary>
    /// The standings embed, shared by <c>/sleeper-standings</c> and anything else
    /// that wants the table.
    ///
    /// Before the draft there are no records to rank, so the embed switches to a
    /// roll call of who has joined and how many slots are still open, which is the
    /// only useful thing to say at that point in the season.
    /// </summary>
    public static Embed BuildStandingsEmbed(
        SleeperLeague league,
        IReadOnlyList<SleeperTeam> teams,
        IReadOnlyDictionary<string, ulong>? links,
        bool allowMentions,
        SleeperNflState? state)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"🏈 {Escape(league.Name)} standings")
            .WithColor(BrandColor);

        var avatar = AvatarUrl(league.Avatar);
        if (avatar is not null) embed.WithThumbnailUrl(avatar);

        if (!league.IsDrafted)
        {
            embed.WithDescription(BuildPreDraftBody(league, teams, links, allowMentions));
            embed.WithFooter($"{league.Season} season, {league.TotalRosters} teams, {league.ScoringLabel}. Standings start once the draft is done.");
            return embed.Build();
        }

        var ranked = SortStandings(teams);
        var sb = new StringBuilder();

        for (var i = 0; i < ranked.Count; i++)
        {
            var t = ranked[i];
            var place = i + 1;

            // The playoff cut is drawn from the league's own playoff_teams setting
            // rather than a hardcoded number, so changing it in Sleeper moves the line.
            if (league.PlayoffTeams > 0 && place == league.PlayoffTeams + 1)
                sb.AppendLine("──────── playoff cut ────────");

            var medal = place switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => "  " };
            sb.AppendLine(
                $"{medal} `{place,2}.` **{TeamLine(t, links, allowMentions)}** " +
                $"`{t.RecordLabel}` · {t.PointsFor:N1} PF · {t.PointsAgainst:N1} PA");

            if (sb.Length > MaxDescription) break;
        }

        embed.WithDescription(sb.Length == 0 ? "No teams yet." : sb.ToString().TrimEnd());

        var weekLabel = state is { IsRegularSeason: true } ? $"Week {state.EffectiveWeek} · " : string.Empty;

        // Only state the playoff picture when the league actually reports one.
        // A league with these unset would otherwise read "top 0 make the playoffs
        // (week 0)", which is worse than saying nothing.
        var playoffLabel = league.PlayoffTeams > 0 && league.PlayoffWeekStart > 0
            ? $" · top {league.PlayoffTeams} make the playoffs (week {league.PlayoffWeekStart})"
            : string.Empty;

        embed.WithFooter($"{weekLabel}{league.ScoringLabel}{playoffLabel}. Ties broken by points for.");

        return embed.Build();
    }

    private static string BuildPreDraftBody(
        SleeperLeague league,
        IReadOnlyList<SleeperTeam> teams,
        IReadOnlyDictionary<string, ulong>? links,
        bool allowMentions)
    {
        var claimed = teams.Where(t => t.Owner is not null).ToList();
        var open = Math.Max(0, league.TotalRosters - claimed.Count);

        var sb = new StringBuilder();
        sb.AppendLine("The draft has not happened yet, so there are no records to rank.");
        sb.AppendLine();
        sb.AppendLine($"**Managers in ({claimed.Count} of {league.TotalRosters}):**");

        if (claimed.Count == 0)
        {
            sb.AppendLine("_Nobody has joined yet._");
        }
        else
        {
            foreach (var t in claimed.OrderBy(t => t.RosterId))
            {
                sb.AppendLine($"• {TeamLine(t, links, allowMentions)}");
                if (sb.Length > MaxDescription) break;
            }
        }

        if (open > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**{open} spot{(open == 1 ? string.Empty : "s")} still open.**");
        }

        return sb.ToString().TrimEnd();
    }

    // ─── Matchups ────────────────────────────────────────────────────────────

    /// <summary>
    /// The week's games. Doubles as the preview (before kickoff), the live
    /// scoreboard (during) and the base of the recap, because the three are the
    /// same table at different points in the week and keeping them one builder is
    /// what stops them drifting apart.
    /// </summary>
    public static Embed BuildMatchupsEmbed(
        SleeperLeague league,
        int week,
        IReadOnlyList<SleeperGame> games,
        IReadOnlyDictionary<string, ulong>? links,
        bool allowMentions,
        string? titlePrefix = null,
        string? footerNote = null)
    {
        var live = games.Any(g => g.HasStarted);
        var prefix = titlePrefix ?? (live ? "Scoreboard" : "Matchups");

        var embed = new EmbedBuilder()
            .WithTitle($"🏈 {Escape(league.Name)} · Week {week} {prefix}")
            .WithColor(BrandColor);

        if (games.Count == 0)
        {
            embed.WithDescription($"No games are scheduled for week {week}.");
            return embed.Build();
        }

        embed.WithDescription(BuildGamesBody(games, links, allowMentions, live));
        embed.WithFooter(footerNote ?? (live
            ? $"{league.ScoringLabel} · closest game first · scores update automatically"
            : $"{league.ScoringLabel} · {games.Count} games"));

        return embed.Build();
    }

    /// <summary>
    /// The games table body. Also used on its own to detect whether anything moved
    /// since the last edit, so pass-through formatting changes are the only thing
    /// that can trigger a spurious edit.
    /// </summary>
    public static string BuildGamesBody(
        IReadOnlyList<SleeperGame> games,
        IReadOnlyDictionary<string, ulong>? links,
        bool allowMentions,
        bool showScores)
    {
        var sb = new StringBuilder();

        foreach (var g in games)
        {
            var home = TeamLine(g.HomeTeam, links, allowMentions);
            var away = TeamLine(g.AwayTeam, links, allowMentions);

            if (!showScores)
            {
                sb.AppendLine($"**{home}**  vs  **{away}**");
                sb.AppendLine($"_{g.HomeTeam.RecordLabel} · {g.AwayTeam.RecordLabel}_");
                sb.AppendLine();
            }
            else
            {
                // Bold the side that is ahead so the leader reads at a glance.
                var homeLead = g.Home.Points > g.Away.Points;
                var awayLead = g.Away.Points > g.Home.Points;

                sb.AppendLine(
                    $"{(homeLead ? "**" : string.Empty)}{home} {g.Home.Points:N2}{(homeLead ? "**" : string.Empty)}" +
                    $"  vs  " +
                    $"{(awayLead ? "**" : string.Empty)}{g.Away.Points:N2} {away}{(awayLead ? "**" : string.Empty)}");

                sb.AppendLine(g.Margin < 0.005
                    ? "_dead level_"
                    : $"_margin {g.Margin:N2}_");
                sb.AppendLine();
            }

            if (sb.Length > MaxDescription)
            {
                sb.AppendLine("_(list truncated)_");
                break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ─── Recap ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The end-of-week recap: final scores plus the three things people actually
    /// argue about (highest score, closest game, worst beating).
    ///
    /// <paramref name="players"/> is used only to name the week's top individual
    /// performer, and is allowed to be empty: when the player cache is off, that
    /// line is simply left out rather than showing a raw player id.
    /// </summary>
    public static Embed BuildRecapEmbed(
        SleeperLeague league,
        int week,
        IReadOnlyList<SleeperGame> games,
        IReadOnlyDictionary<string, ulong>? links,
        bool allowMentions,
        IReadOnlyDictionary<string, SleeperPlayer> players)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"🏁 {Escape(league.Name)} · Week {week} results")
            .WithColor(BrandColor);

        if (games.Count == 0)
        {
            embed.WithDescription($"No games were played in week {week}.");
            return embed.Build();
        }

        var sb = new StringBuilder();
        foreach (var g in games.OrderBy(g => g.MatchupId))
        {
            var winner = g.Leader;
            var homeWon = winner is not null && winner.RosterId == g.HomeTeam.RosterId;
            var awayWon = winner is not null && winner.RosterId == g.AwayTeam.RosterId;

            sb.AppendLine(
                $"{(homeWon ? "**" : string.Empty)}{TeamLine(g.HomeTeam, links, allowMentions)} {g.Home.Points:N2}{(homeWon ? "**" : string.Empty)}" +
                $"  ·  " +
                $"{(awayWon ? "**" : string.Empty)}{g.Away.Points:N2} {TeamLine(g.AwayTeam, links, allowMentions)}{(awayWon ? "**" : string.Empty)}");

            if (sb.Length > MaxDescription) break;
        }
        embed.WithDescription(sb.ToString().TrimEnd());

        // Team of the week: the single highest score across both sides of every game.
        var allSides = games
            .SelectMany(g => new[] { (Team: g.HomeTeam, Side: g.Home), (Team: g.AwayTeam, Side: g.Away) })
            .ToList();

        var best = allSides.OrderByDescending(x => x.Side.Points).First();
        embed.AddField("Highest score",
            $"{TeamLine(best.Team, links, allowMentions)} · **{best.Side.Points:N2}**", inline: true);

        var closest = games.OrderBy(g => g.Margin).First();
        embed.AddField("Closest game",
            $"{Escape(closest.Leader?.Label ?? "tie")} by **{closest.Margin:N2}**", inline: true);

        var blowout = games.OrderByDescending(g => g.Margin).First();
        if (blowout.MatchupId != closest.MatchupId)
        {
            embed.AddField("Biggest blowout",
                $"{Escape(blowout.Leader?.Label ?? "tie")} by **{blowout.Margin:N2}**", inline: true);
        }

        var topPlayer = FindTopPlayer(allSides.Select(x => x.Side), players);
        if (topPlayer is not null)
            embed.AddField("Top performer", topPlayer, inline: false);

        embed.WithFooter($"{league.ScoringLabel} · week {week} final");
        return embed.Build();
    }

    /// <summary>
    /// The highest-scoring individual started anywhere in the league this week.
    /// Returns null when the player cache is off or nobody scored, so the caller
    /// can drop the field rather than print a placeholder.
    /// </summary>
    private static string? FindTopPlayer(
        IEnumerable<SleeperMatchupSide> sides,
        IReadOnlyDictionary<string, SleeperPlayer> players)
    {
        if (players.Count == 0) return null;

        string? bestId = null;
        var bestPoints = 0.0;

        foreach (var side in sides)
        {
            // Starters only: a monster week on someone's bench did not count.
            foreach (var starter in side.Starters)
            {
                if (string.IsNullOrWhiteSpace(starter) || starter == "0") continue;
                if (!side.PlayerPoints.TryGetValue(starter, out var pts)) continue;
                if (pts <= bestPoints) continue;

                bestPoints = pts;
                bestId = starter;
            }
        }

        if (bestId is null || bestPoints <= 0) return null;
        if (!players.TryGetValue(bestId, out var player)) return null;

        var team = string.IsNullOrWhiteSpace(player.Team) ? string.Empty : $", {player.Team}";
        return $"**{Escape(player.Name)}** ({player.Position}{team}) · **{bestPoints:N2}**";
    }
}
