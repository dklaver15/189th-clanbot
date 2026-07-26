using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Two jobs against the clan's Satisfactory factory, both powered by the Ficsit
/// Remote Monitoring mod:
///
///   • <b>Alerts</b> — a fast poll for things somebody needs to know about NOW:
///     a tripped fuse, or a battery bank running down. Posted on the transition
///     into the bad state, and again when it clears.
///   • <b>Digest</b> — a once-a-day factory report: session age, power, AWESOME
///     Sink progress, space-elevator phase, and who's been playing.
///
/// ── Why one service and not two ──
/// They share a client, a channel resolver and an embed style, and the digest is
/// a once-a-day branch inside a loop that has to run anyway. Splitting them would
/// double the boilerplate to save nothing.
///
/// ── Quiet on boot ──
/// The FIRST alert check records state without announcing anything. Otherwise
/// every redeploy would re-announce a fuse that has been tripped for hours, which
/// trains people to ignore the alerts.
///
/// ── Cost ──
/// FRM's web server runs off the game thread, so these reads are cheap — but they
/// are not free, and this deliberately polls only the small endpoints. getFactory
/// and getAll walk the whole save and must never go in a loop.
///
/// ── Gating ──
/// Idle unless <see cref="BotConfig.SatisfactoryEnabled"/> and FRM are both
/// configured. Alerts and the digest are then independently switchable.
/// </summary>
public sealed class SatisfactoryFactoryService : BackgroundService
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    /// <summary>Hysteresis on the battery alert, in percentage points.</summary>
    private const double BatteryRecoveryMargin = 10;

    private readonly DiscordSocketClient _client;
    private readonly FrmApiService _frm;
    private readonly SatisfactoryDigestBuilder _digest;
    private readonly SatisfactoryChartRenderer _charts;
    private readonly IServiceProvider _services;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryFactoryService> _logger;

    /// <summary>Circuits currently in the tripped state, as far as we've announced.</summary>
    private readonly HashSet<int> _fuseAlerted = new();

    /// <summary>Circuits currently in the low-battery state, as far as we've announced.</summary>
    private readonly HashSet<int> _batteryAlerted = new();

    /// <summary>Last time we posted about a circuit, for flap throttling.</summary>
    private readonly Dictionary<int, DateTime> _lastCircuitAlertUtc = new();

    /// <summary>
    /// How late after the scheduled hour the digest will still go out. A bot
    /// that was down at 9am should post when it comes back at 10; a bot that was
    /// down all day should NOT post yesterday's report at 11pm, by which point
    /// it's stale and the "yesterday" framing is confusing.
    /// </summary>
    private static readonly TimeSpan DigestCatchUp = TimeSpan.FromHours(3);

    /// <summary>False until the first successful alert read — see "quiet on boot".</summary>
    private bool _alertStateSeeded;

    /// <summary>
    /// Local date of the last digest SLOT we've handled, so it fires once per
    /// slot. Local rather than UTC: with a 9am Central schedule the UTC date
    /// rolls over mid-afternoon local time, so a UTC-keyed guard would let a
    /// second digest through on the same local day.
    ///
    /// <para>In-memory, and therefore not sufficient on its own — a redeploy
    /// resets it. <see cref="AlreadyPostedAsync"/> is the durable guard; this
    /// field just saves a database round-trip on the polls in between.</para>
    /// </summary>
    private DateOnly? _lastDigestSlot;

    /// <summary>Resolved once and cached — see <see cref="DigestZone"/>.</summary>
    private TimeZoneInfo? _digestZone;

    /// <summary>Slot the attempt counter belongs to; resets when the slot changes.</summary>
    private DateOnly? _digestAttemptSlot;

    /// <summary>
    /// Sends attempted for the current slot. Bounded because retrying is only
    /// worth it for a transient failure, and we can't tell one from a permanent
    /// one — a bot that has lost Send Messages in the digest channel would
    /// otherwise rebuild the whole report every poll for three hours, every day,
    /// forever. Each rebuild costs five FRM reads including getProdStats, which
    /// FrmApiService explicitly warns against polling.
    /// </summary>
    private int _digestPostAttempts;

    private const int MaxDigestPostAttempts = 3;

    /// <summary>
    /// World seed, cached. Every metric sample wants it so a save wipe doesn't
    /// splice two factories into one chart line, but getSessionInfo is a
    /// separate request and the seed changes at most once per save — so it's
    /// refreshed hourly rather than per poll.
    /// </summary>
    private long _cachedSeed;

    private DateTime _seedFetchedUtc = DateTime.MinValue;

    private static readonly TimeSpan SeedRefresh = TimeSpan.FromHours(1);

    /// <summary>Last retention prune, so it runs hourly rather than every poll.</summary>
    private DateTime _lastPruneUtc = DateTime.MinValue;

    /// <summary>
    /// Whether we've already said the digest is waiting on the server, so an
    /// outage spanning the whole window says so once rather than every poll.
    /// </summary>
    private bool _loggedDigestOutage;

    /// <summary>Consecutive sampling failures, for bounded logging.</summary>
    private int _sampleFailures;

    private const int MaxSampleFailureWarnings = 3;

    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    public SatisfactoryFactoryService(
        DiscordSocketClient client,
        FrmApiService frm,
        SatisfactoryDigestBuilder digest,
        SatisfactoryChartRenderer charts,
        IServiceProvider services,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryFactoryService> logger)
    {
        _client = client;
        _frm = frm;
        _digest = digest;
        _charts = charts;
        _services = services;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan PollInterval =>
        TimeSpan.FromSeconds(Math.Clamp(_config.SatisfactoryAlertIntervalSeconds, 30, 3600));

    private TimeSpan AlertCooldown =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryAlertCooldownMinutes, 1, 1440));

    private bool Active => _config.SatisfactoryEnabled && _config.FrmEnabled && _frm.IsConfigured;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try { await Task.Delay(StartupGrace, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!Active)
        {
            _logger.LogInformation(
                "SatisfactoryFactoryService idle (satisfactory={Sat}, frm={Frm})",
                _config.SatisfactoryEnabled, _config.FrmEnabled && _frm.IsConfigured);
            return;
        }

        // No boot-time suppression needed: the window check rejects a late boot
        // on its own, and AlreadyPostedAsync rejects a boot inside the window
        // when the digest has already gone out.
        var zone = DigestZone();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

        _logger.LogInformation(
            "SatisfactoryFactoryService started; polling every {Seconds}s, alerts={Alerts}, digest={Digest} at {Hour}:00 {Zone} (currently {Local:yyyy-MM-dd HH:mm})",
            (int)PollInterval.TotalSeconds, _config.SatisfactoryAlertsEnabled,
            _config.SatisfactoryDigestEnabled, DigestHourLocal(), zone.Id, nowLocal);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.SatisfactoryAlertsEnabled) await CheckAlertsAsync(stoppingToken);
                if (_config.SatisfactoryDigestEnabled) await MaybePostDigestAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SatisfactoryFactoryService tick failed; will retry on next poll");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private int DigestHourLocal() => Math.Clamp(_config.SatisfactoryDigestHour, 0, 23);

    /// <summary>
    /// The timezone the digest hour is expressed in.
    ///
    /// <para>Stored as an IANA id rather than a fixed UTC hour so the post
    /// doesn't slide an hour at each DST changeover — 9am Central is 14:00 UTC
    /// in July and 15:00 UTC in January, and a hardcoded UTC hour is wrong for
    /// roughly half the year whichever value you pick.</para>
    ///
    /// <para>Falls back to UTC on an unrecognised id rather than throwing: a
    /// typo in config should cost a correctly-scheduled digest, not the alert
    /// loop that shares this service. Resolved once — TimeZoneInfo lookups hit
    /// the OS database, and this runs every poll.</para>
    /// </summary>
    private TimeZoneInfo DigestZone()
    {
        if (_digestZone is not null) return _digestZone;

        var id = _config.SatisfactoryDigestTimeZone;

        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) && tz is not null)
        {
            _digestZone = tz;
        }
        else
        {
            _logger.LogWarning(
                "SatisfactoryDigestTimeZone \"{Id}\" not recognised — falling back to UTC. Use an IANA id like America/Chicago.",
                id);
            _digestZone = TimeZoneInfo.Utc;
        }

        return _digestZone;
    }

    // ─── Alerts ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls getPower and announces transitions into and out of the two states
    /// worth waking someone for: a tripped fuse, and a battery bank draining
    /// toward empty.
    /// </summary>
    private async Task CheckAlertsAsync(CancellationToken ct)
    {
        var circuits = await _frm.GetPowerAsync(ct);

        // null is "unknown" — the server or FRM is unreachable. Hold all state:
        // clearing it here would replay every active alert on recovery.
        if (circuits is null) return;

        var now = DateTime.UtcNow;

        foreach (var c in circuits)
        {
            await EvaluateFuseAsync(c, now, ct);
            await EvaluateBatteryAsync(c, now, ct);
        }

        // Record what this read saw, for the power chart. Deliberately here and
        // not in its own loop: getPower has already been called, so sampling is
        // a row insert rather than another request to the game server.
        await RecordSampleAsync(circuits, now, ct);

        // Circuits can disappear (rebuilt, merged, dismantled). Drop their state
        // so a returning circuit id doesn't inherit a stale "already alerted".
        var live = circuits.Select(c => c.CircuitGroupId).ToHashSet();
        _fuseAlerted.RemoveWhere(id => !live.Contains(id));
        _batteryAlerted.RemoveWhere(id => !live.Contains(id));

        // The first successful read only seeds state — see "quiet on boot".
        if (!_alertStateSeeded)
        {
            _alertStateSeeded = true;
            foreach (var c in circuits)
            {
                if (c.FuseTriggered) _fuseAlerted.Add(c.CircuitGroupId);
                if (IsBatteryLow(c)) _batteryAlerted.Add(c.CircuitGroupId);
            }

            if (_fuseAlerted.Count > 0 || _batteryAlerted.Count > 0)
                _logger.LogInformation(
                    "Satisfactory alerts seeded on boot: {Fuses} tripped fuse(s), {Batteries} low batter(ies) — not announced",
                    _fuseAlerted.Count, _batteryAlerted.Count);
        }
    }

    private async Task EvaluateFuseAsync(FrmPowerCircuit c, DateTime now, CancellationToken ct)
    {
        if (!_alertStateSeeded) return;   // seeding pass handles this tick

        var wasAlerted = _fuseAlerted.Contains(c.CircuitGroupId);

        if (c.FuseTriggered && !wasAlerted)
        {
            if (!PassesCooldown(c.CircuitGroupId, now)) return;
            _fuseAlerted.Add(c.CircuitGroupId);

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("⚡ Fuse tripped")
                .WithDescription(
                    $"Circuit **{c.CircuitGroupId}** has blown its fuse — everything on it is offline until someone resets it.")
                .AddField("Draw before it blew", $"{c.PowerMaxConsumed:0.#} MW", true)
                .AddField("Capacity", $"{c.PowerCapacity:0.#} MW", true)
                .WithCurrentTimestamp()
                .Build(), ct, "fuse alert");
        }
        else if (!c.FuseTriggered && wasAlerted)
        {
            _fuseAlerted.Remove(c.CircuitGroupId);

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("⚡ Fuse reset")
                .WithDescription($"Circuit **{c.CircuitGroupId}** is back online.")
                .WithCurrentTimestamp()
                .Build(), ct, "fuse recovery");
        }
    }

    private async Task EvaluateBatteryAsync(FrmPowerCircuit c, DateTime now, CancellationToken ct)
    {
        if (!_alertStateSeeded) return;

        var wasAlerted = _batteryAlerted.Contains(c.CircuitGroupId);
        var low = IsBatteryLow(c);

        if (low && !wasAlerted)
        {
            if (!PassesCooldown(c.CircuitGroupId, now)) return;
            _batteryAlerted.Add(c.CircuitGroupId);

            // BatteryTimeEmpty is an "HH:MM:SS" string from FRM, not a number.
            var eta = string.IsNullOrWhiteSpace(c.BatteryTimeEmpty) || c.BatteryTimeEmpty == "00:00:00"
                ? "unknown"
                : c.BatteryTimeEmpty;

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(new Color(0xF1C40F))
                .WithTitle("🔋 Batteries running down")
                .WithDescription(
                    $"Circuit **{c.CircuitGroupId}** is drawing more than it makes and the batteries are covering the gap.")
                .AddField("Charge left", $"{c.BatteryPercent:0.#}%", true)
                .AddField("Time to empty", eta, true)
                .AddField("Draw vs production", $"{c.PowerConsumed:0.#} / {c.PowerProduction:0.#} MW", true)
                .WithCurrentTimestamp()
                .Build(), ct, "battery alert");
        }
        else if (wasAlerted && c.BatteryPercent > _config.SatisfactoryBatteryAlertPercent + BatteryRecoveryMargin)
        {
            // Hysteresis: recover well above the trigger, so a bank hovering at
            // the threshold doesn't alternate alert/recovery every poll.
            _batteryAlerted.Remove(c.CircuitGroupId);

            await PostAlertAsync(new EmbedBuilder()
                .WithColor(Color.Green)
                .WithTitle("🔋 Batteries recovering")
                .WithDescription($"Circuit **{c.CircuitGroupId}** is charging again — {c.BatteryPercent:0.#}%.")
                .WithCurrentTimestamp()
                .Build(), ct, "battery recovery");
        }
    }

    /// <summary>
    /// Writes one aggregate row for this poll, and occasionally prunes old ones.
    ///
    /// <para>Never throws into the caller. Sampling exists to draw a chart; an
    /// exception here must not cost a fuse alert, which is the thing in this
    /// service that someone is actually waiting on.</para>
    /// </summary>
    private async Task RecordSampleAsync(IReadOnlyList<FrmPowerCircuit> circuits, DateTime now, CancellationToken ct)
    {
        if (!_config.SatisfactoryMetricsEnabled) return;

        try
        {
            // Resolved BEFORE the scope is opened. It can make an HTTP call, and
            // doing that inside the scope would pin a DbContext and its SQLite
            // connection for the length of a network round-trip.
            var seed = await ResolveSeedAsync(ct);

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Only circuits that actually have batteries contribute to the mean.
            // Averaging in every battery-less circuit as 0% would report the grid
            // as flat whenever one Power Storage exists among ten circuits.
            var withBatteries = circuits.Where(c => c.BatteryCapacity > 0).ToList();

            db.SatisfactoryMetricSamples.Add(new SatisfactoryMetricSample
            {
                SampledUtc = now,
                Seed = seed,
                PowerConsumedMw = circuits.Sum(c => c.PowerConsumed),
                PowerCapacityMw = circuits.Sum(c => c.PowerCapacity),
                PowerProductionMw = circuits.Sum(c => c.PowerProduction),
                CircuitCount = circuits.Count,
                TrippedCount = circuits.Count(c => c.FuseTriggered),
                BatteryPercent = withBatteries.Count > 0 ? withBatteries.Average(c => c.BatteryPercent) : null,
            });

            await db.SaveChangesAsync(ct);

            _sampleFailures = 0;

            await MaybePruneSamplesAsync(db, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Bounded logging. A persistent failure — most likely the migration
            // not applied on this host — happens on every poll, and 720 identical
            // warnings a day buries everything else in the log.
            _sampleFailures++;

            if (_sampleFailures <= MaxSampleFailureWarnings)
                _logger.LogWarning(ex,
                    "Failed to record a Satisfactory power sample ({Count} of {Max} warnings; further failures log at debug)",
                    _sampleFailures, MaxSampleFailureWarnings);
            else
                _logger.LogDebug(ex, "Failed to record a Satisfactory power sample");
        }
    }

    /// <summary>
    /// Deletes samples past the retention window, at most once an hour.
    ///
    /// <para>Without this the table grows forever at ~720 rows/day. It's small
    /// per row, but "small forever" is still a leak, and the chart queries only
    /// ever look back 30 days.</para>
    /// </summary>
    private async Task MaybePruneSamplesAsync(BotDbContext db, DateTime now, CancellationToken ct)
    {
        if (now - _lastPruneUtc < PruneInterval) return;
        _lastPruneUtc = now;

        var days = Math.Clamp(_config.SatisfactoryMetricsRetentionDays, 1, 365);
        var cutoff = now - TimeSpan.FromDays(days);

        var removed = await db.SatisfactoryMetricSamples
            .Where(s => s.SampledUtc < cutoff)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            _logger.LogInformation("Pruned {Count} Satisfactory power sample(s) older than {Days}d", removed, days);
    }

    /// <summary>
    /// The world seed, refreshed hourly. Returns 0 when it can't be determined —
    /// the sample is still worth keeping for its power figures, and the model
    /// documents 0 as "unknown".
    /// </summary>
    private async Task<long> ResolveSeedAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _seedFetchedUtc < SeedRefresh) return _cachedSeed;

        // Stamp BEFORE the call, not after a successful one. Stamping only on
        // success means a persistently failing getSessionInfo is retried every
        // poll rather than hourly — and this path is only reached when getPower
        // already succeeded, so the failure it hits is "FRM is up but this one
        // endpoint is broken", which is a lasting state, not a blip. Each retry
        // would cost a full HTTP timeout on the alert loop.
        _seedFetchedUtc = DateTime.UtcNow;

        var session = await _frm.GetSessionInfoAsync(ct);
        if (session is null) return _cachedSeed;   // keep the last known value

        _cachedSeed = session.Seed;
        return _cachedSeed;
    }

    /// <summary>
    /// A circuit counts as low only if it actually HAS batteries. Without the
    /// capacity check every circuit reads as 0% and would alert forever — which
    /// is the clan's current state, since nobody has built a battery yet.
    /// </summary>
    private bool IsBatteryLow(FrmPowerCircuit c) =>
        c.BatteryCapacity > 0 && c.BatteryPercent <= _config.SatisfactoryBatteryAlertPercent;

    /// <summary>
    /// Throttles repeat alerts for one circuit. Recoveries deliberately bypass
    /// this — "it's fixed" should never be suppressed.
    /// </summary>
    private bool PassesCooldown(int circuitId, DateTime now)
    {
        if (_lastCircuitAlertUtc.TryGetValue(circuitId, out var last) && now - last < AlertCooldown)
            return false;

        _lastCircuitAlertUtc[circuitId] = now;
        return true;
    }

    // ─── Daily digest ────────────────────────────────────────────────────────

    /// <summary>
    /// Fires the digest once per slot, at or shortly after the configured hour.
    ///
    /// <para><b>The window is anchored to the slot INSTANT, not to
    /// <c>TimeOfDay</c>.</b> A naive <c>TimeOfDay &gt;= hour &amp;&amp;
    /// TimeOfDay &lt; hour + 3</c> silently truncates when the window crosses
    /// midnight — at hour 22 it's really 2 hours, at hour 23 it's 1, because
    /// TimeOfDay can never reach 25:00. Subtracting two instants has no such
    /// boundary. The clan's hour is 9 so this isn't live today, but a
    /// scheduling bug that only appears for certain config values is exactly
    /// the kind that gets found at 11pm.</para>
    ///
    /// <para>The window exists at all because an exact hour match loses the day
    /// whenever the poll straddles the hour boundary or the bot is redeployed at
    /// 8:58am — which, given how often this bot ships, is not a rare event.</para>
    /// </summary>
    private async Task MaybePostDigestAsync(CancellationToken ct)
    {
        var zone = DigestZone();
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);

        // The most recent slot instant that has actually passed. If today's
        // hasn't arrived yet, the live slot is yesterday's — which is what lets
        // a late-evening hour catch up past midnight.
        var slot = DateOnly.FromDateTime(nowLocal).ToDateTime(new TimeOnly(DigestHourLocal(), 0));
        if (nowLocal < slot) slot = slot.AddDays(-1);

        if (nowLocal - slot >= DigestCatchUp) return;   // window has closed

        var slotDate = DateOnly.FromDateTime(slot);
        if (_lastDigestSlot == slotDate) return;        // already handled this process

        if (_digestAttemptSlot != slotDate)
        {
            _digestAttemptSlot = slotDate;
            _digestPostAttempts = 0;
            _loggedDigestOutage = false;
        }

        if (_digestPostAttempts >= MaxDigestPostAttempts)
        {
            // Already burned the budget on this slot; wait for tomorrow.
            return;
        }

        var day = PreviousLocalDay(slotDate, zone);

        // Durable guard. _lastDigestSlot is in-memory, so on its own it would
        // let every redeploy inside the window post another copy — and this bot
        // is redeployed often enough that "inside a 3-hour window" is a routine
        // event, not a corner case. A snapshot row for the reporting day is
        // proof the digest already went out.
        if (await AlreadyPostedAsync(day, ct))
        {
            _lastDigestSlot = slotDate;
            return;
        }

        // No "posting…" line here: this runs on every poll inside the catch-up
        // window, and an FRM outage turned it into 26 identical claims that a
        // digest was being posted when none ever was. PostDigestAsync logs once
        // it actually has something to send.
        //
        // The guard is set only when the post is believed delivered, so a server
        // that's unreachable at 9:00 is retried on the next poll instead of
        // costing the day. Uncertain counts as delivered — see PostOutcome.
        var outcome = await PostDigestAsync(day, ct);

        if (outcome != PostOutcome.Failed)
            _lastDigestSlot = slotDate;
    }

    /// <summary>
    /// What we believe happened to a message we tried to send.
    ///
    /// <para><see cref="Uncertain"/> is the interesting one. A send that times
    /// out may well have arrived — Discord accepted it and we lost the response,
    /// or the rate limiter held us past the deadline. Retrying that would post
    /// the digest twice, which people notice; treating it as delivered risks
    /// losing one day's report, which they mostly don't. So an ambiguous result
    /// stops the retry loop but is logged loudly enough to explain a missing
    /// post.</para>
    /// </summary>
    private enum PostOutcome
    {
        Sent,
        Failed,
        Uncertain,
    }

    /// <summary>
    /// Has a digest already been posted for this reporting day?
    ///
    /// <para>Keyed on the reporting date ALONE, deliberately not on the world
    /// seed. If the save were wiped between two runs the seeds would differ, and
    /// a seed-scoped check would happily post a second digest for the same
    /// morning. "Did we already report on Friday" is a question about the
    /// calendar, not about the world.</para>
    ///
    /// <para>Fails OPEN — an exception here returns false, so a database problem
    /// costs a duplicate post at worst rather than silently killing the feature.
    /// The in-memory guard still holds within the process.</para>
    /// </summary>
    private async Task<bool> AlreadyPostedAsync(DigestDay day, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var key = day.Key;
            return await db.SatisfactoryDailySnapshots.AnyAsync(s => s.LocalDate == key, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check whether the {Date} digest was already posted; assuming not", day.Key);
            return false;
        }
    }

    /// <summary>
    /// The UTC interval covering the local day before <paramref name="slotDate"/>.
    ///
    /// <para>Both bounds go through <see cref="TimeZoneInfo"/> rather than being
    /// derived by subtracting 24 hours, because a DST changeover day is 23 or 25
    /// hours long. Getting this wrong would quietly drop or double-count an hour
    /// of playtime twice a year.</para>
    ///
    /// <para>Local midnight is never inside a US DST gap, but
    /// <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/>
    /// throws on an invalid local time, and this runs for whatever zone is in
    /// config — so ambiguous and invalid instants are resolved rather than
    /// allowed to take down the poll loop.</para>
    /// </summary>
    private static DigestDay PreviousLocalDay(DateOnly slotDate, TimeZoneInfo zone)
    {
        var yesterday = slotDate.AddDays(-1);

        return new DigestDay(
            yesterday,
            LocalMidnightToUtc(yesterday, zone),
            LocalMidnightToUtc(slotDate, zone));
    }

    private static DateTime LocalMidnightToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);

        // Invalid = the clock skipped this instant (spring forward). Step
        // forward until it exists; an hour is always enough for real zones.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(15);

        // Ambiguous = the clock repeated it (fall back). ConvertTimeToUtc picks
        // standard time, which is the later of the two — deterministic, and the
        // same choice on both ends of the window, so the day stays contiguous.
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// Builds and posts the digest. Returns true only if the message actually
    /// reached Discord.
    ///
    /// <para>The snapshot row is committed AFTER the post, never before. It
    /// doubles as the "already reported on this day" marker, so writing it
    /// during the build would record intent rather than delivery: a 403 or a
    /// timeout on the send would leave the marker down, every later attempt
    /// would see it and skip, and the day would be silently lost.</para>
    /// </summary>
    private async Task<PostOutcome> PostDigestAsync(DigestDay day, CancellationToken ct)
    {
        var result = await _digest.BuildDailyAsync(day, ct);
        if (result is null)
        {
            // Doesn't count against the send budget — the build never reached
            // Discord, and an unreachable FRM costs one small request to retry.
            //
            // Debug, not Information: this repeats every poll for as long as the
            // window is open, so a three-hour outage writes ~90 identical lines.
            // The one-per-day summary below is what's worth reading.
            _logger.LogDebug("Satisfactory digest not built — server unreachable, will retry");

            if (!_loggedDigestOutage)
            {
                _loggedDigestOutage = true;
                _logger.LogInformation(
                    "Satisfactory digest for {Date} is waiting on the server — retrying until the window closes",
                    day.Key);
            }

            return PostOutcome.Failed;
        }

        _loggedDigestOutage = false;
        _digestPostAttempts++;

        _logger.LogInformation("Posting Satisfactory daily digest for {Date}", day.Key);

        // Same zone the reporting day was computed in, so the axis and the
        // "Covering Friday, July 24" heading agree about where the day ends.
        var zone = DigestZone();

        // Chart is best-effort and comes back null on any failure, including
        // "not enough samples yet". A null just means the embed posts alone.
        var chart = _config.SatisfactoryMetricsEnabled
            ? await _charts.TryRenderPowerChartAsync(TimeSpan.FromHours(24), zone, day.Label, ct)
            : null;

        var embed = result.Embed;

        if (chart is not null)
        {
            // Discord ties an embed image to an attachment by filename, so this
            // string MUST match the name passed to SendFileAsync below.
            embed = embed.ToEmbedBuilder()
                .WithImageUrl($"attachment://{DigestChartFileName}")
                .Build();
        }

        var outcome = await PostDigestEmbedAsync(embed, chart, ct);

        if (outcome == PostOutcome.Failed)
        {
            _logger.LogWarning(
                "Satisfactory digest for {Date} was built but not posted (attempt {Attempt} of {Max})",
                day.Key, _digestPostAttempts, MaxDigestPostAttempts);
            return outcome;
        }

        if (outcome == PostOutcome.Uncertain)
            _logger.LogWarning(
                "Satisfactory digest for {Date} may or may not have posted; not retrying to avoid a duplicate",
                day.Key);

        if (result.Snapshot is not null)
            await _digest.CommitSnapshotAsync(result.Snapshot, ct);

        return outcome;
    }

    // ─── Posting ─────────────────────────────────────────────────────────────

    /// <summary>Alerts go to the presence feed channel — same audience, same urgency band.</summary>
    private Task<PostOutcome> PostAlertAsync(Embed embed, CancellationToken ct, string what) =>
        PostToAsync(_config.SatisfactoryFeedChannelId, embed, ct, what);

    /// <summary>
    /// The digest goes to its own channel when one is set, else the feed channel.
    /// A daily report and a join/leave feed often want different homes.
    /// </summary>
    private const string DigestChartFileName = "power.png";

    private Task<PostOutcome> PostDigestEmbedAsync(Embed embed, byte[]? chart, CancellationToken ct) =>
        PostToAsync(
            _config.SatisfactoryDigestChannelId != 0
                ? _config.SatisfactoryDigestChannelId
                : _config.SatisfactoryFeedChannelId,
            embed, ct, "digest", chart, DigestChartFileName);

    /// <summary>
    /// Posts an embed. The digest path uses the result to decide whether the day
    /// has really been reported, so a swallowed exception must not read as
    /// success — and an ambiguous timeout must not read as failure.
    /// </summary>
    private async Task<PostOutcome> PostToAsync(
        ulong channelId, Embed embed, CancellationToken ct, string what,
        byte[]? attachment = null, string? attachmentName = null)
    {
        if (channelId == 0)
        {
            // Not an error for alerts (the feed is optional), but for the digest
            // it means misconfiguration, and silence here made that invisible.
            _logger.LogDebug("SatisfactoryFactoryService: no channel configured for {What}", what);
            return PostOutcome.Failed;
        }

        try
        {
            var channel = _client.GetChannel(channelId) as IMessageChannel
                          ?? await _client.Rest.GetChannelAsync(channelId, new RequestOptions { CancelToken = ct }) as IMessageChannel;

            if (channel is null)
            {
                _logger.LogWarning("SatisfactoryFactoryService: could not resolve channel {ChannelId} for {What}", channelId, what);
                return PostOutcome.Failed;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            var options = new RequestOptions { CancelToken = cts.Token };

            if (attachment is not null && attachmentName is not null)
            {
                using var stream = new MemoryStream(attachment);
                using var file = new FileAttachment(stream, attachmentName);

                await channel.SendFileAsync(
                    file,
                    embed: embed,
                    allowedMentions: AllowedMentions.None,
                    options: options);
            }
            else
            {
                await channel.SendMessageAsync(
                    embed: embed,
                    allowedMentions: AllowedMentions.None,
                    options: options);
            }

            return PostOutcome.Sent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Our own 30s deadline, not a shutdown. The send may already have
            // landed — Discord.NET's rate limiter can hold a request past the
            // deadline after the message was queued.
            _logger.LogWarning(ex, "SatisfactoryFactoryService: {What} timed out; delivery unknown", what);
            return PostOutcome.Uncertain;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SatisfactoryFactoryService: failed to post {What}", what);
            return PostOutcome.Failed;
        }
    }

    /// <summary>
    /// Session and item names are builder-controlled text landing in a Discord
    /// message. Neutralize markdown so they can't forge formatting or a mass-ping.
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
