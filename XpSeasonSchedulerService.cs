using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Opens seasons that were lined up for later, and closes ones that have reached
/// their scheduled end.
///
/// ── Why this exists ──
/// Seasons used to be 100% manual, which meant a season starting at midnight on
/// the 1st required somebody to be awake at midnight on the 1st to type the
/// command. Everything else about the manual model is kept: a season still only
/// opens and closes when an officer decided it should, this just moves the
/// decision earlier than the moment. Nothing here invents a season, extends one,
/// or rolls one over into the next. A closed season leaves the ladder idle exactly
/// as a hand-closed one does, so continuing is always a fresh decision.
///
/// ── Why it is its own service rather than part of XpAccrualService ──
/// Accrual runs every ten minutes, and ten minutes of slop either side of a season
/// boundary is visible: members earning into a season that "ended" at midnight, or
/// a launch announcement landing at 00:07. This ticks every half minute, which is
/// cheap (two indexed queries per guild) and puts the boundary within a rounding
/// error of the time on the poster. Keeping it separate also means a slow or stuck
/// accrual pass cannot delay a season boundary and vice versa.
///
/// ── On being late ──
/// If the bot is down over a boundary, the transition happens on the first tick
/// after it comes back rather than being skipped. That is deliberate for both
/// directions. An opening season keeps its SCHEDULED StartUtc, so accrual's
/// lookback clamp still pays from the intended instant rather than from whenever
/// the process happened to recover. A closing season closes late, which is the
/// safe direction: XP earned in the overrun is counted rather than silently
/// dropped, and the results card still gets posted instead of a season vanishing
/// with no record.
/// </summary>
public sealed class XpSeasonSchedulerService : BackgroundService
{
    /// <summary>
    /// How often to look for a due transition. Half a minute keeps the worst-case
    /// error on a boundary under a minute while costing two indexed queries per
    /// guild per tick, both of which miss in the common case.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Matches the other XP services. Discord.NET populates guilds and the member
    /// cache asynchronously after connecting, and a season transition wants a
    /// usable guild for the announcement and the board refresh.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly XpService _xp;
    private readonly XpLeaderboardService _board;
    private readonly BotConfig _config;
    private readonly ILogger<XpSeasonSchedulerService> _logger;

    public XpSeasonSchedulerService(
        IServiceProvider services,
        DiscordSocketClient client,
        XpService xp,
        XpLeaderboardService board,
        IOptions<BotConfig> config,
        ILogger<XpSeasonSchedulerService> logger)
    {
        _services = services;
        _client = client;
        _xp = xp;
        _board = board;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.XpEnabled)
        {
            _logger.LogInformation("XpSeasonSchedulerService: XpEnabled is false, service idle.");
            return;
        }

        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _logger.LogInformation("XpSeasonSchedulerService started, checking every {Sec}s for a due season transition",
            TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var guild in _client.Guilds.ToList())
            {
                try
                {
                    await OpenDueSeasonAsync(guild, stoppingToken);
                    await CloseDueSeasonAsync(guild, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Never let one guild's failure stop the loop. A season boundary
                    // that throws once must still be retried on the next tick, or the
                    // season silently never opens.
                    _logger.LogError(ex, "XP season scheduler failed for guild {Guild}", guild.Id);
                }
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Flips a scheduled season to Active, then refreshes the board and announces
    /// it. The status flip is committed BEFORE either of those, so a failure to
    /// announce (missing channel, missing permission, Discord having a bad day)
    /// leaves the season open and accruing rather than rolling it back. A season
    /// that opened quietly is a nuisance; one that failed to open because the
    /// announcement failed is a lost day of XP.
    /// </summary>
    private async Task OpenDueSeasonAsync(SocketGuild guild, CancellationToken ct)
    {
        XpSeason? opened;

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            opened = await _xp.ActivateDueSeasonAsync(db, guild.Id, ct);
        }

        if (opened is null) return;

        _logger.LogInformation("XP: Season {Number} opened on schedule in guild {Guild}", opened.Number, guild.Id);

        _board.InvalidateBoard(guild.Id);
        try { await _board.RefreshAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "XP: board refresh failed after opening Season {Number}", opened.Number); }

        if (!await _board.PostSeasonStartAsync(guild, opened, ct))
        {
            _logger.LogWarning(
                "XP: Season {Number} is open but the start announcement did not post. Check XpLevelUpAnnounceChannelId.",
                opened.Number);
        }
    }

    /// <summary>
    /// Closes a season that has reached its scheduled end: standings locked into
    /// lifetime records, board redrawn to the idle state, results card posted.
    ///
    /// Same ordering rule as opening. The close is committed first and the card is
    /// posted after, because a season that failed to close would keep accruing past
    /// the date everyone was told it ended, which is far worse than a card that has
    /// to be chased up in the log.
    /// </summary>
    private async Task CloseDueSeasonAsync(SocketGuild guild, CancellationToken ct)
    {
        XpSeason finished;
        IReadOnlyList<XpStanding> standings;

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            if (await _xp.GetSeasonDueToCloseAsync(db, guild.Id, ct) is null) return;

            var result = await _xp.EndSeasonAsync(db, guild.Id, ct);
            if (result is null) return;

            finished  = result.Value.Season;
            standings = result.Value.Standings;
        }

        _logger.LogInformation("XP: Season {Number} closed on schedule in guild {Guild}, {Count} member(s) placed",
            finished.Number, guild.Id, standings.Count);

        _board.InvalidateBoard(guild.Id);
        try { await _board.RefreshAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "XP: board refresh failed after closing Season {Number}", finished.Number); }

        if (!await _board.PostSeasonResultsAsync(guild, finished, standings, ct))
        {
            _logger.LogWarning(
                "XP: Season {Number} closed but the results card did not post. Check XpLevelUpAnnounceChannelId.",
                finished.Number);
        }
    }
}
