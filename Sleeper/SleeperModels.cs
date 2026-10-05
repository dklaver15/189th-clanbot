namespace ClanGuardBot.Models;

/// <summary>
/// The league's own settings, read once per cycle from
/// <c>GET /league/{league_id}</c>.
///
/// Everything the surfaces display about the league (name, team count, scoring
/// flavour, when the playoffs start) is read from here rather than hardcoded, so
/// changing the league in Sleeper changes what the bot says without a redeploy.
/// </summary>
public sealed record SleeperLeague(
    string LeagueId,
    string Name,
    string Season,
    string SeasonType,
    string Status,
    int TotalRosters,
    int PlayoffWeekStart,
    int PlayoffTeams,
    double PointsPerReception,
    string? DraftId,
    string? Avatar)
{
    /// <summary>
    /// Human label for the reception scoring, derived from the league's own
    /// <c>scoring_settings.rec</c> so it can never drift from reality.
    /// </summary>
    public string ScoringLabel => PointsPerReception switch
    {
        <= 0 => "Standard",
        > 0 and < 1 => "Half-PPR",
        1 => "PPR",
        _ => $"{PointsPerReception:0.##} PPR",
    };

    /// <summary>True once the draft is done and the league is playing games.</summary>
    public bool IsDrafted => !string.Equals(Status, "pre_draft", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(Status, "drafting", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One member of the league, from <c>GET /league/{league_id}/users</c>.
/// <see cref="TeamName"/> is the custom team name if they set one, which lives in
/// Sleeper's free-form <c>metadata</c> bag and is frequently absent.
/// </summary>
public sealed record SleeperLeagueUser(
    string UserId,
    string DisplayName,
    string? TeamName,
    string? Avatar);

/// <summary>
/// One team's season record, from <c>GET /league/{league_id}/rosters</c>, joined to
/// its owner.
///
/// ── Sleeper splits points across two integers ──
/// The API reports points as <c>fpts</c> (whole) plus <c>fpts_decimal</c>
/// (hundredths), never as one number. <see cref="PointsFor"/> and
/// <see cref="PointsAgainst"/> are the recombined values, which is the only form
/// anything downstream should use.
///
/// <see cref="OwnerId"/> is null for an orphan team (a roster slot nobody has
/// claimed yet). The league is 16 teams and fills up over time, so every surface
/// has to render an unclaimed roster rather than assume an owner exists.
/// </summary>
public sealed record SleeperTeam(
    int RosterId,
    string? OwnerId,
    SleeperLeagueUser? Owner,
    int Wins,
    int Losses,
    int Ties,
    double PointsFor,
    double PointsAgainst,
    int WaiverPosition,
    IReadOnlyList<string> Starters,
    IReadOnlyList<string> Players)
{
    /// <summary>
    /// What to call this team: the custom team name, else the owner's Sleeper
    /// display name, else a placeholder for an unclaimed roster slot.
    /// </summary>
    public string Label =>
        !string.IsNullOrWhiteSpace(Owner?.TeamName) ? Owner!.TeamName!
        : !string.IsNullOrWhiteSpace(Owner?.DisplayName) ? Owner!.DisplayName
        : $"Team {RosterId} (open)";

    /// <summary>The manager behind the team, for lines that name a person rather than a team.</summary>
    public string ManagerLabel =>
        !string.IsNullOrWhiteSpace(Owner?.DisplayName) ? Owner!.DisplayName : "unclaimed";

    public string RecordLabel => Ties > 0 ? $"{Wins}-{Losses}-{Ties}" : $"{Wins}-{Losses}";

    public int GamesPlayed => Wins + Losses + Ties;
}

/// <summary>
/// One team's side of one week's game, from
/// <c>GET /league/{league_id}/matchups/{week}</c>.
///
/// Sleeper returns a flat list with one entry per roster; the two sides of a game
/// are the entries sharing a <see cref="MatchupId"/>. A null MatchupId means that
/// roster has no game that week (a bye in an odd-sized league, or an off week).
/// </summary>
public sealed record SleeperMatchupSide(
    int RosterId,
    int? MatchupId,
    double Points,
    IReadOnlyList<string> Starters,
    IReadOnlyDictionary<string, double> PlayerPoints);

/// <summary>A head-to-head game: two <see cref="SleeperMatchupSide"/> rows joined to their teams.</summary>
public sealed record SleeperGame(
    int MatchupId,
    SleeperTeam HomeTeam,
    SleeperMatchupSide Home,
    SleeperTeam AwayTeam,
    SleeperMatchupSide Away)
{
    public double Margin => Math.Abs(Home.Points - Away.Points);

    /// <summary>The leading side, or null when the two sides are exactly level.</summary>
    public SleeperTeam? Leader =>
        Home.Points > Away.Points ? HomeTeam
        : Away.Points > Home.Points ? AwayTeam
        : null;

    public bool HasStarted => Home.Points > 0 || Away.Points > 0;
}

/// <summary>
/// The NFL's own clock, from <c>GET /state/nfl</c>. This is the authority on which
/// week is live; the bot never computes a week from the calendar.
///
/// <see cref="SeasonType"/> is "pre", "regular" or "post". During the preseason
/// <see cref="Week"/> is 0 while <see cref="DisplayWeek"/> already reads 1, which
/// is why the posting service gates on the season type before doing anything.
/// </summary>
public sealed record SleeperNflState(
    int Week,
    int DisplayWeek,
    int Leg,
    string Season,
    string SeasonType,
    DateTime? SeasonStartDate)
{
    public bool IsRegularSeason => string.Equals(SeasonType, "regular", StringComparison.OrdinalIgnoreCase);
    public bool IsPostSeason    => string.Equals(SeasonType, "post", StringComparison.OrdinalIgnoreCase);
    public bool IsPreSeason     => string.Equals(SeasonType, "pre", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The week that fantasy surfaces should show. Falls back to DisplayWeek when
    /// Week is still 0, so a preseason lookup shows week 1 instead of week 0.
    /// </summary>
    public int EffectiveWeek => Week > 0 ? Week : Math.Max(1, DisplayWeek);
}

/// <summary>
/// A trimmed NFL player, kept in memory so scores can name who scored them.
///
/// ── Why trimmed ──
/// Sleeper's player directory is a single ~5 MB document covering every player in
/// the league, and the bot runs on a 2 GB box where RAM, not CPU, is the binding
/// constraint. Only these four fields are retained; the response is parsed as a
/// stream so the full document is never materialised as a string.
/// </summary>
public sealed record SleeperPlayer(
    string PlayerId,
    string Name,
    string Position,
    string Team);
