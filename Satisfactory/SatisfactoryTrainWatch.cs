using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Watches the clan's rail network for the two things worth interrupting
/// somebody about, and records what it saw for the freight chart.
///
///   • <b>Derailed</b>. Unambiguous, reported by the game, and the one failure
///     that will not fix itself.
///   • <b>Stopped on a route</b>. A self-driving train that has not moved for
///     the configured window. Usually no path, no power, or a blocked block
///     signal, and it silently starves whatever it feeds.
///
/// ── Why this isn't a BackgroundService ──
/// It rides <see cref="SatisfactoryFactoryService"/>'s existing poll instead of
/// starting a second loop against the same server. That service already owns the
/// cadence, the connection wait, the quiet-on-boot rule and the posting path with
/// its delivery semantics, and none of that is worth a second copy. This class
/// decides WHAT to say; the caller decides where it goes and whether it arrived.
///
/// ── Why it returns embeds instead of posting them ──
/// So the failure contract stays in one place. The factory service distinguishes
/// sent, failed and uncertain; a second posting path here would inevitably get
/// that subtly different, and the difference only shows up during an outage.
///
/// ── Quiet on boot ──
/// The first successful read seeds state and announces nothing, matching the
/// power alerts. Otherwise every redeploy re-announces a train that derailed
/// hours ago, which is how people learn to ignore a feed.
///
/// ── What it deliberately does NOT alert on ──
/// A stationary train with no timetable is somebody's manual train parked in a
/// siding, not a fault. A bare diagnostic code on a MOVING train is also not a
/// fault: FRM's docs never enumerate those codes, so an unknown healthy value
/// would otherwise alert forever. Codes are shown as context on an alert that
/// fired for a reason we can defend, never as the trigger.
/// </summary>
public sealed class SatisfactoryTrainWatch
{
    private readonly FrmApiService _frm;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryTrainWatch> _logger;

    /// <summary>Trains currently derailed, as far as we've announced.</summary>
    private readonly HashSet<string> _derailAlerted = new();

    /// <summary>Trains currently flagged as stopped, as far as we've announced.</summary>
    private readonly HashSet<string> _stoppedAlerted = new();

    /// <summary>
    /// When each train was first seen stationary. Cleared the moment it moves,
    /// so the dwell clock only ever measures one continuous stop.
    /// </summary>
    private readonly Dictionary<string, DateTime> _stationarySince = new();

    /// <summary>Last time we posted about a train, for flap throttling.</summary>
    private readonly Dictionary<string, DateTime> _lastAlertUtc = new();

    /// <summary>False until the first successful read. See "quiet on boot".</summary>
    private bool _seeded;

    /// <summary>Last station read, so the heavier endpoint runs on its own cadence.</summary>
    private DateTime _lastStationPollUtc = DateTime.MinValue;

    private int _sampleFailures;

    private const int MaxSampleFailureWarnings = 3;

    public SatisfactoryTrainWatch(
        FrmApiService frm,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryTrainWatch> logger)
    {
        _frm = frm;
        _services = services;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan StuckAfter =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryTrainStuckMinutes, 2, 240));

    private TimeSpan StationPollInterval =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryTrainStationPollMinutes, 1, 240));

    private TimeSpan AlertCooldown =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryAlertCooldownMinutes, 1, 1440));

    /// <summary>
    /// One pass over the network. Returns the embeds the caller should post, in
    /// the order they should appear.
    ///
    /// <para>Never throws for anything short of cancellation: this runs inside
    /// the same tick as the fuse alerts, and a rail problem must not cost a
    /// power alert somebody is actually waiting on.</para>
    /// </summary>
    /// <param name="seed">World seed for the sample row, 0 when unknown.</param>
    public async Task<IReadOnlyList<Embed>> EvaluateAsync(long seed, CancellationToken ct)
    {
        if (!_config.SatisfactoryTrainAlertsEnabled && !_config.SatisfactoryMetricsEnabled)
            return Array.Empty<Embed>();

        var trains = await _frm.GetTrainsAsync(ct);

        // null is "unknown", not "no trains". Hold every bit of state: clearing
        // it here would replay every active alert the moment FRM answers again.
        if (trains is null) return Array.Empty<Embed>();

        var now = DateTime.UtcNow;
        var alerts = new List<Embed>();

        UpdateStationaryClocks(trains, now);

        if (_config.SatisfactoryTrainAlertsEnabled)
        {
            if (_seeded)
            {
                foreach (var train in trains)
                {
                    EvaluateDerail(train, now, alerts);
                    EvaluateStopped(train, now, alerts);
                }
            }

            Forget(trains);
            SeedIfNeeded(trains, now);
        }

        // Stations are read on their own slower cadence and only when there is
        // something to do with them. Both readers are optional, so this can skip
        // entirely on most ticks.
        var stations = _config.SatisfactoryMetricsEnabled && DueForStations(now)
            ? await ReadStationsAsync(now, ct)
            : null;

        await RecordSampleAsync(trains, stations, seed, now, ct);

        return alerts;
    }

    // ─── Alerts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Keeps the "how long has this been sitting still" clock, for every train,
    /// on every tick.
    ///
    /// <para>Runs BEFORE the seeding check and outside the alert gate, which is
    /// the point: a train that was already stopped when the bot booted has no
    /// history, and starting its clock at boot means the first alert fires a
    /// window later rather than immediately. That is the correct trade. The
    /// alternative, treating unknown dwell as infinite, announces a stopped
    /// train on every redeploy.</para>
    /// </summary>
    private void UpdateStationaryClocks(IReadOnlyList<FrmTrain> trains, DateTime now)
    {
        foreach (var train in trains)
        {
            var key = SatisfactoryRail.KeyOf(train);

            if (SatisfactoryRail.IsStationary(train))
            {
                if (!_stationarySince.ContainsKey(key)) _stationarySince[key] = now;
            }
            else
            {
                _stationarySince.Remove(key);
            }
        }
    }

    private void EvaluateDerail(FrmTrain train, DateTime now, List<Embed> alerts)
    {
        var key = SatisfactoryRail.KeyOf(train);
        var name = SatisfactoryRail.DisplayName(train);
        var wasAlerted = _derailAlerted.Contains(key);

        if (train.Derailed && !wasAlerted)
        {
            if (!PassesCooldown(key, now)) return;
            _derailAlerted.Add(key);

            var embed = new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("🚂 Train derailed")
                .WithDescription(
                    $"**{Escape(name)}** has come off the track. It will not move again until somebody " +
                    "re-rails it in game, and anything on its route stops getting deliveries until then.")
                .WithCurrentTimestamp();

            AddRouteContext(embed, train);
            AddCodeContext(embed, train);

            alerts.Add(embed.Build());
        }
        else if (!train.Derailed && wasAlerted)
        {
            _derailAlerted.Remove(key);

            // Recoveries bypass the cooldown on purpose. "It is fixed" is never
            // the message to suppress.
            alerts.Add(new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("🚂 Train back on the rails")
                .WithDescription($"**{Escape(name)}** is running again.")
                .WithCurrentTimestamp()
                .Build());
        }
        else if (train.PendingDerail && !train.Derailed && !wasAlerted)
        {
            // Folded in here rather than given its own state set: a pending
            // collision either becomes a derail on a later tick, in which case
            // the red alert above supersedes it, or it clears on its own and was
            // never worth a recovery message. The cooldown key is shared, so the
            // warning cannot double up with the derail alert for the same train.
            if (!PassesCooldown(key, now)) return;

            alerts.Add(new EmbedBuilder()
                .WithColor(new Color(0xF1C40F))
                .WithTitle("🚂 Collision ahead")
                .WithDescription(
                    $"**{Escape(name)}** is reporting a pending collision. Two trains are heading for the " +
                    "same block, and one of them is about to come off.")
                .WithCurrentTimestamp()
                .Build());
        }
    }

    /// <summary>
    /// A self-driving train that has not moved for the configured window.
    ///
    /// <para><b>Gated on having a route</b> (see
    /// <see cref="SatisfactoryRail.HasRoute"/>) so a manually-driven train parked
    /// in a siding never fires. <b>And gated on a dwell window</b> rather than on
    /// the diagnostic codes, because a train loading at a platform is legitimately
    /// stationary for a few seconds and the codes are undocumented. The default
    /// window is minutes, which no normal dwell reaches.</para>
    /// </summary>
    private void EvaluateStopped(FrmTrain train, DateTime now, List<Embed> alerts)
    {
        var key = SatisfactoryRail.KeyOf(train);
        var name = SatisfactoryRail.DisplayName(train);
        var wasAlerted = _stoppedAlerted.Contains(key);

        // A derailed train is stationary by definition. Reporting both would be
        // two alerts for one problem, and the derail is the useful one.
        if (train.Derailed)
        {
            _stoppedAlerted.Remove(key);
            return;
        }

        var stoppedFor = _stationarySince.TryGetValue(key, out var since) ? now - since : TimeSpan.Zero;
        var stuck = SatisfactoryRail.HasRoute(train) && stoppedFor >= StuckAfter;

        if (stuck && !wasAlerted)
        {
            if (!PassesCooldown(key, now)) return;
            _stoppedAlerted.Add(key);

            var embed = new EmbedBuilder()
                .WithColor(new Color(0xF1C40F))
                .WithTitle("🚂 Train hasn't moved")
                .WithDescription(
                    $"**{Escape(name)}** has been sitting still for {Humanize(stoppedFor)} with a route to run.")
                .WithCurrentTimestamp();

            AddRouteContext(embed, train);
            AddCodeContext(embed, train);

            // The three usual causes, in the order they are worth checking. This
            // is the difference between an alert and an answer, and it is the
            // reason the codes above are worth surfacing raw.
            embed.AddField("Usually one of",
                "No power on the track, a block signal it can't clear, or no path to its next stop "
                + "(a switch thrown the wrong way, or track removed).");

            alerts.Add(embed.Build());
        }
        else if (!stuck && wasAlerted)
        {
            _stoppedAlerted.Remove(key);

            alerts.Add(new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("🚂 Train moving again")
                .WithDescription($"**{Escape(name)}** is back under way.")
                .WithCurrentTimestamp()
                .Build());
        }
    }

    private static void AddRouteContext(EmbedBuilder embed, FrmTrain train)
    {
        var next = SatisfactoryRail.NextStop(train);
        if (!string.IsNullOrWhiteSpace(next))
            embed.AddField("Next stop", Escape(next!), true);

        if (train.MaxPayloadMass > 0)
            embed.AddField("Carrying",
                $"{100.0 * train.PayloadMass / train.MaxPayloadMass:0}% of capacity", true);
    }

    /// <summary>
    /// Attaches whatever diagnostic codes the game is reporting, verbatim.
    ///
    /// <para>Verbatim because they are undocumented. Rewording an unknown code
    /// into friendly prose would mean inventing a meaning for it, and the reader
    /// can search the exact string; they cannot search our paraphrase.</para>
    /// </summary>
    private static void AddCodeContext(EmbedBuilder embed, FrmTrain train)
    {
        var codes = new List<string>();

        if (SatisfactoryRail.Code(train.Status) is { } status) codes.Add($"Status: `{status}`");
        if (SatisfactoryRail.Code(train.SelfDriving) is { } driving) codes.Add($"Self-driving: `{driving}`");
        if (SatisfactoryRail.Code(train.Docking) is { } docking) codes.Add($"Docking: `{docking}`");
        if (SatisfactoryRail.Code(train.Path) is { } path) codes.Add($"Path: `{path}`");

        if (codes.Count > 0)
            embed.AddField("What the game says", string.Join("\n", codes));
    }

    /// <summary>
    /// Drops state for trains that no longer exist, so a reused actor id can't
    /// inherit a stale "already alerted" and go permanently silent.
    /// </summary>
    private void Forget(IReadOnlyList<FrmTrain> trains)
    {
        var live = trains.Select(SatisfactoryRail.KeyOf).ToHashSet();

        _derailAlerted.RemoveWhere(k => !live.Contains(k));
        _stoppedAlerted.RemoveWhere(k => !live.Contains(k));

        foreach (var gone in _stationarySince.Keys.Where(k => !live.Contains(k)).ToList())
            _stationarySince.Remove(gone);

        foreach (var gone in _lastAlertUtc.Keys.Where(k => !live.Contains(k)).ToList())
            _lastAlertUtc.Remove(gone);
    }

    private void SeedIfNeeded(IReadOnlyList<FrmTrain> trains, DateTime now)
    {
        if (_seeded) return;
        _seeded = true;

        foreach (var train in trains)
        {
            var key = SatisfactoryRail.KeyOf(train);
            if (train.Derailed) _derailAlerted.Add(key);

            // A train that is already stopped at boot is seeded as alerted only
            // if it has a route, matching the condition that would have fired.
            if (!train.Derailed
                && SatisfactoryRail.HasRoute(train)
                && SatisfactoryRail.IsStationary(train))
            {
                _stoppedAlerted.Add(key);
                _stationarySince[key] = now;
            }
        }

        if (_derailAlerted.Count > 0 || _stoppedAlerted.Count > 0)
            _logger.LogInformation(
                "Satisfactory train alerts seeded on boot: {Derailed} derailed, {Stopped} stopped. Not announced.",
                _derailAlerted.Count, _stoppedAlerted.Count);
    }

    private bool PassesCooldown(string key, DateTime now)
    {
        if (_lastAlertUtc.TryGetValue(key, out var last) && now - last < AlertCooldown) return false;

        _lastAlertUtc[key] = now;
        return true;
    }

    // ─── Stations ────────────────────────────────────────────────────────────

    private bool DueForStations(DateTime now) => now - _lastStationPollUtc >= StationPollInterval;

    private async Task<IReadOnlyList<FrmTrainStation>?> ReadStationsAsync(DateTime now, CancellationToken ct)
    {
        // Stamped before the call, not after a success. Stamping only on success
        // means a persistently broken endpoint is retried on every tick, and each
        // retry costs a full HTTP timeout on a loop that owes its answer to the
        // fuse alerts sharing it.
        _lastStationPollUtc = now;

        return await _frm.GetTrainStationsAsync(ct);
    }

    /// <summary>
    /// Counts platforms sitting at either extreme.
    ///
    /// <para>Returns counts rather than a verdict. Whether a full platform is a
    /// problem depends on which end of the line it is, and the station board is
    /// the place that decides that (see
    /// <see cref="SatisfactoryRail.IsLoading"/>); a sample row only needs the
    /// magnitude so the trend is chartable.</para>
    /// </summary>
    private (int Starved, int BackedUp) CountExtremes(IReadOnlyList<FrmTrainStation> stations)
    {
        var starvedBelow = Math.Clamp(_config.SatisfactoryStationStarvedPercent, 0, 100) / 100.0;
        var backedAbove = Math.Clamp(_config.SatisfactoryStationBackedUpPercent, 0, 100) / 100.0;

        var starved = 0;
        var backed = 0;

        foreach (var platform in stations.SelectMany(s => s.CargoInventory ?? Array.Empty<FrmStationPlatform>()))
        {
            if (SatisfactoryRail.Fill(platform) is not { } fill) continue;

            // A loading platform that is empty is starved; an unloading platform
            // that is empty is just a platform waiting for its next train, which
            // is the normal state and not worth counting.
            if (fill <= starvedBelow && SatisfactoryRail.IsLoading(platform)) starved++;
            if (fill >= backedAbove) backed++;
        }

        return (starved, backed);
    }

    // ─── Sampling ────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes one row for this poll. Never throws into the caller: sampling
    /// exists to draw a chart, and an exception here must not cost the derail
    /// alert that shares the tick.
    /// </summary>
    private async Task RecordSampleAsync(
        IReadOnlyList<FrmTrain> trains,
        IReadOnlyList<FrmTrainStation>? stations,
        long seed,
        DateTime now,
        CancellationToken ct)
    {
        if (!_config.SatisfactoryMetricsEnabled) return;

        try
        {
            var extremes = stations is null ? ((int, int)?)null : CountExtremes(stations);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            db.SatisfactoryTrainSamples.Add(new SatisfactoryTrainSample
            {
                SampledUtc = now,
                Seed = seed,
                TrainCount = trains.Count,
                MovingCount = trains.Count(t => !SatisfactoryRail.IsStationary(t)),
                DerailedCount = trains.Count(t => t.Derailed),

                // Read off the SAME state the alerts used this tick, rather than
                // recomputed from config, so a chart and a feed can never
                // disagree about one instant.
                StuckCount = _stoppedAlerted.Count,

                PayloadMass = trains.Sum(t => t.PayloadMass),
                MaxPayloadMass = trains.Sum(t => t.MaxPayloadMass),

                StationCount = stations?.Count,
                StarvedPlatforms = extremes?.Item1,
                BackedUpPlatforms = extremes?.Item2,
            });

            await db.SaveChangesAsync(ct);

            _sampleFailures = 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bounded, for the same reason the power sampler bounds its own: the
            // likeliest cause is a migration not applied on this host, which
            // fails identically on every poll and would bury the log.
            _sampleFailures++;

            if (_sampleFailures <= MaxSampleFailureWarnings)
                _logger.LogWarning(ex,
                    "Failed to record a Satisfactory train sample ({Count} of {Max} warnings; further failures log at debug)",
                    _sampleFailures, MaxSampleFailureWarnings);
            else
                _logger.LogDebug(ex, "Failed to record a Satisfactory train sample");
        }
    }

    /// <summary>
    /// Prunes train samples past the retention window. Called by the factory
    /// service's hourly prune so all three sample tables age out together.
    /// </summary>
    public static Task<int> PruneAsync(BotDbContext db, DateTime cutoff, CancellationToken ct) =>
        db.SatisfactoryTrainSamples.Where(s => s.SampledUtc < cutoff).ExecuteDeleteAsync(ct);

    // ─── Shared ──────────────────────────────────────────────────────────────

    private static string Humanize(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{Math.Max(1, (int)span.TotalMinutes)} min";

    /// <summary>
    /// Train and station names are player-controlled text landing in a Discord
    /// message. Neutralize markdown so a name can't forge formatting or a ping.
    /// </summary>
    private static string Escape(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? "(unnamed)"
            : s.Replace("\\", "\\\\")
               .Replace("*", "\\*")
               .Replace("_", "\\_")
               .Replace("~", "\\~")
               .Replace("`", "\\`")
               .Replace("|", "\\|")
               .Replace("@", "@​")
               .Replace("\n", " ")
               .Replace("\r", " ");
}
