using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>
/// One local calendar day, expressed as the UTC half-open interval
/// <c>[StartUtc, EndUtc)</c> that covers it.
///
/// Half-open matters: a session that begins at exactly midnight belongs to the
/// new day and must not be counted twice. And the interval is NOT always 24
/// hours — on DST changeover days it is 23 or 25, which is precisely why the
/// bounds are computed by <see cref="TimeZoneInfo"/> rather than by adding a day.
/// </summary>
public readonly record struct DigestDay(DateOnly LocalDate, DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>How the day is written in the embed, e.g. "Friday, July 24".</summary>
    public string Label => LocalDate.ToDateTime(TimeOnly.MinValue).ToString("dddd, MMMM d");

    /// <summary>Sortable key stored on the snapshot row.</summary>
    public string Key => LocalDate.ToString("yyyy-MM-dd");
}

/// <summary>
/// A built digest: the embed to post, and the snapshot row to persist once it
/// has been posted. <see cref="Snapshot"/> is null for the on-demand report,
/// which records nothing.
/// </summary>
public sealed record DigestResult(Embed Embed, SatisfactoryDailySnapshot? Snapshot);

/// <summary>
/// Builds the Satisfactory factory report embed.
///
/// ── Two shapes, one builder ──
/// <list type="bullet">
/// <item><b>On demand</b> (<c>/satisfactory-report</c>) — pass a null day. Pure
/// snapshot: what the factory looks like right now.</item>
/// <item><b>Daily digest</b> — pass yesterday's <see cref="DigestDay"/>. The
/// report leads with what HAPPENED that day (who played, what got unlocked, what
/// moved) and then shows current state underneath.</item>
/// </list>
///
/// ── Why the live sections stay in the daily report ──
/// Only playtime and unlocks are stored per-event, so only they can be windowed
/// exactly. Power, production and the sink are live reads with no history in FRM.
/// Rather than drop them from the morning post, they're rendered as current
/// state and — where a previous snapshot exists — annotated with the change
/// since it was taken. That's an honest rendering of what we can actually know:
/// precise for events, approximate for levels.
///
/// ── Why this is its own class ──
/// Two callers want the same embed, and resolving a hosted service from DI to
/// call a method on it quietly constructs a SECOND instance unless the
/// registration is written very carefully. A stateless builder can't have that
/// problem.
///
/// ── Failure semantics ──
/// Returns null when the server is unreachable, matching
/// <see cref="FrmApiService"/>. Individual sections are best-effort: a section
/// whose endpoint failed is omitted rather than rendered empty, so a partial
/// outage produces a shorter report instead of one full of zeroes.
/// </summary>
public sealed class SatisfactoryDigestBuilder
{
    /// <summary>How far back "recently active" looks on the on-demand report.</summary>
    private static readonly TimeSpan PlayerWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Oldest snapshot still worth comparing against. A gap longer than this
    /// (bot down for a week, save idle) makes "since the last report" a lie, so
    /// the delta block is dropped rather than mislabelled.
    /// </summary>
    private static readonly TimeSpan MaxDeltaAge = TimeSpan.FromDays(3);

    private readonly FrmApiService _frm;
    private readonly SatisfactoryApiService _api;
    private readonly IServiceProvider _services;
    private readonly ILogger<SatisfactoryDigestBuilder> _logger;

    public SatisfactoryDigestBuilder(
        FrmApiService frm,
        SatisfactoryApiService api,
        IServiceProvider services,
        ILogger<SatisfactoryDigestBuilder> logger)
    {
        _frm = frm;
        _api = api;
        _services = services;
        _logger = logger;
    }

    /// <summary>On-demand report: current state, no reporting window.</summary>
    public async Task<Embed?> BuildAsync(CancellationToken ct = default) =>
        (await BuildCoreAsync(null, ct))?.Embed;

    /// <summary>
    /// The daily digest for <paramref name="day"/>, plus the snapshot row that
    /// should be persisted <b>once the embed has actually been posted</b>.
    ///
    /// <para>The snapshot is deliberately NOT saved here. It doubles as the
    /// "we already reported on this day" marker, and writing it during the build
    /// records intent rather than delivery: if Discord then returned a 403 or
    /// timed out, the marker would already be down, every later attempt would
    /// see it and skip, and the day would be lost for good while the logs
    /// claimed success. Handing the row back lets the caller commit it only
    /// after the post lands.</para>
    /// </summary>
    public Task<DigestResult?> BuildDailyAsync(DigestDay day, CancellationToken ct = default) =>
        BuildCoreAsync(day, ct);

    /// <summary>
    /// Builds the report. Null means the server (or FRM) couldn't be reached at
    /// all — callers should say so rather than posting an empty embed.
    /// </summary>
    /// <param name="day">
    /// The local day being reported on, or null for a plain live snapshot.
    /// </param>
    private async Task<DigestResult?> BuildCoreAsync(DigestDay? day, CancellationToken ct = default)
    {
        // ── The anchor read, and why it needs a fallback ──
        //
        // FRM answers 503 "World not ready" whenever the dedicated server has
        // idled with nobody connected — its world context goes invalid and every
        // endpoint refuses. That is the NORMAL state at 9am: the digest reports
        // on a day that is over, at an hour when nobody is playing. Bailing here
        // meant the morning report failed on exactly the mornings it was for.
        //
        // The vanilla game API on the game port keeps answering while the world
        // is idle — it's what the host's own dashboard reads — so it can still
        // supply the session name and tier. The retrospective sections below
        // come from our database and never needed a live server at all.
        var session = await _frm.GetSessionInfoAsync(ct);
        SatisfactoryServerState? state = null;

        if (session is null)
        {
            state = await _api.GetServerStateAsync(ct);

            if (state is null)
            {
                // Both APIs silent: the machine really is unreachable.
                _logger.LogInformation("Satisfactory digest not built — server unreachable");
                return null;
            }

            _logger.LogInformation(
                "Satisfactory digest: FRM is idle (world not loaded), building from stored data and the game API");
        }

        // Live factory figures only exist when FRM is answering. Left null
        // otherwise, and every section below already omits itself on null.
        var power = session is null ? null : await _frm.GetPowerAsync(ct);
        var prod = session is null ? null : await _frm.GetProdStatsAsync(ct);
        var sink = session is null ? null : await _frm.GetResourceSinkAsync(ct);
        var elevators = session is null ? null : await _frm.GetSpaceElevatorAsync(ct);

        var sessionName = session?.SessionName ?? state?.ActiveSessionName ?? "";
        var seed = session?.Seed ?? 0;

        var embed = new EmbedBuilder()
            .WithColor(new Color(0xE59344))
            .WithCurrentTimestamp();

        // Titles are capped: WithTitle throws above 256 characters, and the
        // session name is player-chosen text that Escape can more than double.
        if (day is { } titleDay)
        {
            embed.WithTitle(Title($"🏭 Daily report — {Escape(sessionName)}"));
            embed.WithDescription($"Covering **{titleDay.Label}**.");
        }
        else
        {
            embed.WithTitle(Title($"🏭 Factory report — {Escape(sessionName)}"));
        }

        // ── What happened during the reporting day ──
        //
        // Every one of these touches the database, and all three are wrapped:
        // a missing table (migration not yet applied on this host), a locked
        // file, a corrupt row — none of that should cost the whole post. The
        // live sections below need no database at all, so a degraded report is
        // strictly better than an exception swallowed by the poll loop, which
        // would look exactly like "the digest feature silently does nothing".
        if (day is { } reportDay)
        {
            await SafelyAsync("playtime", ct, () => AddDayPlaytimeFieldAsync(embed, reportDay, ct));
            await SafelyAsync("unlocks", ct, () => AddDayUnlocksFieldAsync(embed, seed, reportDay, ct));

            var previous = await LoadPreviousSnapshotAsync(seed, reportDay, ct);
            if (session is not null) AddDeltaField(embed, previous, power, prod, sink, session);
        }

        // ── Current state ──
        //
        // TotalPlayDurationText is typed non-nullable, but System.Text.Json does
        // not enforce that on a positional record — a getSessionInfo response
        // missing the key yields null, and EmbedFieldBuilder throws on a null or
        // whitespace value. That would take out the whole digest AND
        // /satisfactory-report, so it goes through the same guard as the rest.
        if (session is not null)
        {
            embed.AddField("Day", session.PassedDays.ToString(), true);
            AddInlineField(embed, "Total playtime", session.TotalPlayDurationText);
            embed.AddField("Deaths", session.NumberOfDaysSinceLastDeath == 0
                ? "someone died today"
                : $"{session.NumberOfDaysSinceLastDeath} day(s) clean", true);
        }
        else if (state is not null)
        {
            // What the game API can still tell us with the world idle.
            embed.AddField("Tier", state.TechTier.ToString(), true);
            AddInlineField(embed, "Phase", PhaseName(state.GamePhase));
            embed.AddField("Server", state.IsGamePaused ? "idle — nobody on" : "running", true);
        }

        AddPowerField(embed, power);
        AddProductionField(embed, prod);
        AddSinkField(embed, sink);
        AddElevatorField(embed, elevators);

        if (day is null)
            await SafelyAsync("recent players", ct, () => AddRecentPlayersFieldAsync(embed, ct));

        embed.WithFooter(session is null
            ? "The factory was idle at posting time, so live power and production aren't available — everything above is from the day itself."
            : day is null
                ? "Live figures from the server at the time of posting."
                : "Playtime and unlocks cover the day above. Power, production and sink are live figures from this morning.");

        // Still written when FRM was idle: the row doubles as the "already
        // reported this day" marker, and every measurement on it is nullable
        // precisely so a partial one is recordable. It contributes no deltas.
        var snapshot = day is { } snapshotDay
            ? BuildSnapshot(seed, session?.PassedDays ?? 0, snapshotDay, power, prod, sink)
            : null;

        // Build() enforces the 6000-character total across title, description
        // and every field. Each field is capped individually but nothing caps
        // the sum, so an unusually busy day is the one way to exceed it. Falling
        // back to a stripped embed keeps the report alive; throwing here would
        // be swallowed by the poll loop and look like the feature doing nothing.
        try
        {
            return new DigestResult(embed.Build(), snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Satisfactory digest exceeded Discord's embed limits; posting a reduced version");

            var fallback = new EmbedBuilder()
                .WithTitle(Title($"🏭 Factory report — {Escape(sessionName)}"))
                .WithColor(new Color(0xE59344))
                .WithCurrentTimestamp()
                .WithDescription(day is { } d
                    ? $"Covering **{d.Label}**. The full report was too long for one message."
                    : "The full report was too long for one message.")
                .Build();

            return new DigestResult(fallback, snapshot);
        }
    }

    // ─── The reporting day: playtime ─────────────────────────────────────────

    /// <summary>
    /// Who played during the reporting day, and for how long.
    ///
    /// <para><b>Sessions are clipped to the window, not filtered by start
    /// time.</b> Somebody who logs on at 10pm and plays past midnight has one
    /// session spanning two days; counting it wholly in either one would be
    /// wrong. The query pulls anything that OVERLAPS the window, then each
    /// session contributes only the part that falls inside it. That also means a
    /// still-open session (EndedUtc null) contributes correctly — its extent is
    /// LastSeenUtc, which the presence poller keeps current.</para>
    /// </summary>
    private async Task AddDayPlaytimeFieldAsync(EmbedBuilder embed, DigestDay day, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        // COALESCE(EndedUtc, LastSeenUtc) is the session's extent. EF Core
        // translates the null-coalescing operator, so this stays server-side.
        var overlapping = await db.SatisfactorySessions
            .Where(s => s.StartedUtc < day.EndUtc && (s.EndedUtc ?? s.LastSeenUtc) > day.StartUtc)
            .ToListAsync(ct);

        var byPlayer = overlapping
            .GroupBy(s => s.PlayerName, StringComparer.Ordinal)
            .Select(g => new
            {
                Name = g.Key,
                Total = g.Aggregate(TimeSpan.Zero, (acc, s) => acc + Clip(s, day)),
                Sessions = g.Count()
            })
            .Where(p => p.Total > TimeSpan.Zero)
            .OrderByDescending(p => p.Total)
            .Take(10)
            .ToList();

        if (byPlayer.Count == 0)
        {
            embed.AddField("Playtime", "Nobody was on the server.", false);
            return;
        }

        var lines = byPlayer.Select(p =>
        {
            var sessions = p.Sessions == 1 ? "" : $" ({p.Sessions} sessions)";
            return $"• **{Escape(p.Name)}** — {SatisfactoryPresenceService.Humanize(p.Total)}{sessions}";
        });

        var text = string.Join("\n", lines);

        if (byPlayer.Count > 1)
        {
            var combined = byPlayer.Aggregate(TimeSpan.Zero, (acc, p) => acc + p.Total);
            text += $"\n\n{SatisfactoryPresenceService.Humanize(combined)} combined across {byPlayer.Count} players.";
        }

        AddField(embed, "Playtime", text);
    }

    /// <summary>
    /// The portion of a session that falls inside the reporting day. Never
    /// negative: the query already guarantees overlap, but clamping keeps a
    /// clock skew or a bad row from producing a negative span that silently eats
    /// someone else's time out of the combined total.
    /// </summary>
    private static TimeSpan Clip(SatisfactorySession s, DigestDay day)
    {
        var extent = s.EndedUtc ?? s.LastSeenUtc;

        var start = s.StartedUtc > day.StartUtc ? s.StartedUtc : day.StartUtc;
        var end = extent < day.EndUtc ? extent : day.EndUtc;

        var span = end - start;
        return span > TimeSpan.Zero ? span : TimeSpan.Zero;
    }

    // ─── The reporting day: unlocks ──────────────────────────────────────────

    /// <summary>
    /// Milestones and research completed during the reporting day.
    ///
    /// <para>CompletedUtc is when the BOT first saw it, not when it truly
    /// completed — the API exposes no completion timestamp. With the research
    /// poll at 5 minutes and milestones at 30, something finished at 11:50pm can
    /// land in the next day's report. The alternative (no recap at all) is worse,
    /// and the unlock feed announces these live anyway; this section is the
    /// summary, not the notification.</para>
    /// </summary>
    private async Task AddDayUnlocksFieldAsync(EmbedBuilder embed, long seed, DigestDay day, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var unlocks = await db.SatisfactoryUnlocks
            .Where(u => u.Seed == seed && u.CompletedUtc >= day.StartUtc && u.CompletedUtc < day.EndUtc)
            .OrderBy(u => u.TechTier)
            .ThenBy(u => u.Name)
            .ToListAsync(ct);

        if (unlocks.Count == 0) return;

        var milestones = unlocks.Where(u => u.Kind == SatisfactoryUnlockKinds.Milestone).ToList();
        var research = unlocks.Where(u => u.Kind == SatisfactoryUnlockKinds.Research).ToList();

        if (milestones.Count > 0)
            AddField(embed, $"🏁 Milestones ({milestones.Count})", Summarize(milestones));

        if (research.Count > 0)
            AddField(embed, $"🔬 Research ({research.Count})", Summarize(research));
    }

    /// <summary>
    /// Renders an unlock list, capped so a big research binge can't blow the
    /// 1024-character embed field limit. Discord rejects the whole message at
    /// that boundary, so the cap is ours and the overflow is stated explicitly.
    /// </summary>
    private static string Summarize(IReadOnlyList<SatisfactoryUnlock> items)
    {
        const int max = 12;

        var shown = items.Take(max)
            .Select(u => u.TechTier > 0
                ? $"• {Escape(u.Name)} *(tier {u.TechTier})*"
                : $"• {Escape(u.Name)}");

        var text = string.Join("\n", shown);

        if (items.Count > max)
            text += $"\n• …and {items.Count - max} more";

        return text;
    }

    // ─── The reporting day: movement ─────────────────────────────────────────

    /// <summary>
    /// Most recent snapshot for this save that predates the end of the reporting
    /// day — i.e. yesterday morning's, in the normal case.
    ///
    /// Bounded by <see cref="MaxDeltaAge"/>: after a long outage the nearest
    /// snapshot might be a week old, and labelling a week of progress as the
    /// last day's is worse than saying nothing.
    /// </summary>
    private async Task<SatisfactoryDailySnapshot?> LoadPreviousSnapshotAsync(
        long seed, DigestDay day, CancellationToken ct)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var floor = day.EndUtc - MaxDeltaAge;

            return await db.SatisfactoryDailySnapshots
                .Where(s => s.Seed == seed && s.TakenUtc < day.EndUtc && s.TakenUtc >= floor)
                .OrderByDescending(s => s.TakenUtc)
                .FirstOrDefaultAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Same reasoning as the day sections: no deltas beats no digest.
            _logger.LogWarning(ex, "Satisfactory digest: could not load the previous snapshot; skipping the delta block");
            return null;
        }
    }

    /// <summary>
    /// What moved since the last snapshot. Every line is conditional: a figure
    /// that didn't change, or that either side failed to measure, is omitted
    /// rather than printed as "+0" — a report full of zeroes reads as a broken
    /// integration.
    /// </summary>
    private static void AddDeltaField(
        EmbedBuilder embed,
        SatisfactoryDailySnapshot? previous,
        IReadOnlyList<FrmPowerCircuit>? power,
        IReadOnlyList<FrmProdStat>? prod,
        IReadOnlyList<FrmResourceSink>? sink,
        FrmSessionInfo session)
    {
        if (previous is null) return;

        var lines = new List<string>();

        var days = session.PassedDays - previous.PassedDays;
        if (days > 0) lines.Add($"• {days} in-game day(s) passed");

        var s = sink?.FirstOrDefault();

        if (s is not null && previous.SinkTotalPoints is { } oldPoints)
        {
            var gained = s.TotalPoints - oldPoints;
            if (gained > 0) lines.Add($"• **{gained:N0}** sink points banked");
        }

        if (s is not null && previous.SinkCoupons is { } oldCoupons)
        {
            var earned = s.NumCoupon - oldCoupons;
            if (earned > 0) lines.Add($"• **{earned}** new coupon(s)");
        }

        if (power is not null && previous.PowerCapacityMw is { } oldCapacity)
        {
            var added = power.Sum(c => c.PowerCapacity) - oldCapacity;
            if (added >= 1) lines.Add($"• **+{added:0.#} MW** of generating capacity");
            else if (added <= -1) lines.Add($"• {added:0.#} MW of capacity (dismantled or offline)");
        }

        if (prod is not null && previous.ProducingItemCount is { } oldItems)
        {
            var added = prod.Count(p => p.CurrentProd > 0.01) - oldItems;
            if (added > 0) lines.Add($"• **{added}** new item(s) coming off the lines");
        }

        if (lines.Count == 0) return;

        AddField(embed, "📈 Since the last report", string.Join("\n", lines));
    }

    /// <summary>
    /// Assembles — but does not save — the snapshot row for this reporting day.
    /// Pure, so it can't fail and can't leave a marker behind for a post that
    /// never happened. See <see cref="BuildDailyAsync"/> for why that matters.
    /// </summary>
    private static SatisfactoryDailySnapshot BuildSnapshot(
        long seed,
        int passedDays,
        DigestDay day,
        IReadOnlyList<FrmPowerCircuit>? power,
        IReadOnlyList<FrmProdStat>? prod,
        IReadOnlyList<FrmResourceSink>? sink)
    {
        var sinkRow = sink?.FirstOrDefault();

        return new SatisfactoryDailySnapshot
        {
            Seed = seed,
            LocalDate = day.Key,
            TakenUtc = DateTime.UtcNow,
            PassedDays = passedDays,

            PowerCapacityMw = power?.Sum(c => c.PowerCapacity),
            PowerConsumedMw = power?.Sum(c => c.PowerConsumed),
            CircuitCount = power?.Count,

            SinkTotalPoints = sinkRow?.TotalPoints,
            SinkCoupons = sinkRow?.NumCoupon,

            ProducingItemCount = prod?.Count(p => p.CurrentProd > 0.01),
            TotalProductionPerMin = prod?.Where(p => p.CurrentProd > 0.01).Sum(p => p.CurrentProd),
        };
    }

    /// <summary>
    /// Persists a snapshot produced by <see cref="BuildDailyAsync"/>. Call this
    /// only after the digest has been posted — the row doubles as the
    /// already-reported marker.
    ///
    /// <para>Idempotent on (Seed, LocalDate): if a digest is somehow built twice
    /// for the same reporting day, the existing row is updated rather than
    /// duplicated, so tomorrow's delta still finds exactly one predecessor.</para>
    ///
    /// <para>Never throws into the caller. A snapshot that fails to save costs
    /// one day of deltas, and risks one duplicate post if the bot is redeployed
    /// inside the catch-up window. An exception here would cost more.</para>
    /// </summary>
    public async Task CommitSnapshotAsync(SatisfactoryDailySnapshot snapshot, CancellationToken ct = default)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var seed = snapshot.Seed;
            var key = snapshot.LocalDate;

            var row = await db.SatisfactoryDailySnapshots
                .FirstOrDefaultAsync(x => x.Seed == seed && x.LocalDate == key, ct);

            if (row is null)
            {
                db.SatisfactoryDailySnapshots.Add(snapshot);
            }
            else
            {
                row.TakenUtc = snapshot.TakenUtc;
                row.PassedDays = snapshot.PassedDays;
                row.PowerCapacityMw = snapshot.PowerCapacityMw;
                row.PowerConsumedMw = snapshot.PowerConsumedMw;
                row.CircuitCount = snapshot.CircuitCount;
                row.SinkTotalPoints = snapshot.SinkTotalPoints;
                row.SinkCoupons = snapshot.SinkCoupons;
                row.ProducingItemCount = snapshot.ProducingItemCount;
                row.TotalProductionPerMin = snapshot.TotalProductionPerMin;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not a database fault — don't log it as one.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to record the Satisfactory daily snapshot for {Date} — tomorrow's deltas will be missing",
                snapshot.LocalDate);
        }
    }

    /// <summary>
    /// Runs a section that touches the database, turning any failure into a
    /// missing field instead of a missing report. Shutdown still propagates —
    /// swallowing cancellation would make a stopping host look like a bug.
    /// </summary>
    private async Task SafelyAsync(string section, CancellationToken ct, Func<Task> build)
    {
        try
        {
            await build();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Satisfactory digest: {Section} section failed; omitting it", section);
        }
    }

    // ─── Live sections ───────────────────────────────────────────────────────

    /// <summary>
    /// Grid load, as consumption against generating capacity.
    ///
    /// <para><b>PowerProduction is shown only when non-zero.</b> It used to read
    /// 0 on this server while circuits drew tens of megawatts, which printed as
    /// "83.9 / 180 MW used, 0 MW produced" — a factory that should have tripped.
    /// It populates now (975 MW on 2026-07-25), so the guard is no longer
    /// hiding it; keep the guard anyway, since circuits with no generation at
    /// all still report 0 and "0 MW being generated" is noise on those.</para>
    ///
    /// <para>Worth knowing when reading it: production BELOW consumption is the
    /// supply side failing to keep up — generators starved of fuel or water —
    /// not a measurement quirk.</para>
    /// </summary>
    private static void AddPowerField(EmbedBuilder embed, IReadOnlyList<FrmPowerCircuit>? power)
    {
        if (power is null || power.Count == 0) return;

        var production = power.Sum(c => c.PowerProduction);
        var consumed = power.Sum(c => c.PowerConsumed);
        var capacity = power.Sum(c => c.PowerCapacity);
        var tripped = power.Count(c => c.FuseTriggered);

        var text = "";

        if (capacity > 0)
        {
            var load = consumed / capacity;

            // Bar first, numbers second. At a glance you want "are we close to
            // the ceiling", and only then the megawatts.
            text += $"{Bar(load)}  **{100.0 * load:0}%**\n";
        }

        text += $"{consumed:0.#} / {capacity:0.#} MW across {power.Count} circuit(s)";

        if (production > 0) text += $"\n{production:0.#} MW being generated";
        if (tripped > 0) text += $"\n⚡ **{tripped} tripped fuse(s)**";

        AddField(embed, "Power", text);
    }

    /// <summary>
    /// Top items by current output, with how hard each line is running.
    ///
    /// getProdStats lists every item the save knows about, and in an early-game
    /// factory most sit at zero — so this filters to things actually producing
    /// and says nothing rather than printing a wall of "0.0".
    /// </summary>
    private static void AddProductionField(EmbedBuilder embed, IReadOnlyList<FrmProdStat>? prod)
    {
        if (prod is null) return;

        var producing = prod
            .Where(p => p.CurrentProd > 0.01)
            .OrderByDescending(p => p.CurrentProd)
            .Take(5)
            .ToList();

        if (producing.Count == 0) return;

        var lines = producing.Select(p =>
        {
            // MaxProd is 0 for consume-only items (Leaves, for one) and can be 0
            // transiently — never divide without this guard.
            var pct = p.MaxProd > 0 ? $" ({100.0 * p.CurrentProd / p.MaxProd:0}% of max)" : "";
            return $"• **{Escape(p.Name)}** — {p.CurrentProd:0.#}/min{pct}";
        });

        AddField(embed, "Top production", string.Join("\n", lines));
    }

    /// <summary>
    /// A text meter. Uses U+2588 / U+2591, which are full-cell block glyphs —
    /// every font Discord falls back to renders them at the same width, so the
    /// bar stays rectangular. Characters that only LOOK equal width (▰▱) drift
    /// on mobile and the bar comes out ragged.
    /// </summary>
    /// <param name="fraction">
    /// Can exceed 1: a circuit drawing more than it can make is exactly the
    /// state worth showing. The bar saturates, the percentage beside it doesn't.
    /// </param>
    private static string Bar(double fraction, int width = 16)
    {
        if (double.IsNaN(fraction) || fraction < 0) fraction = 0;

        var filled = (int)Math.Round(Math.Min(fraction, 1.0) * width);
        return new string('█', filled) + new string('░', width - filled);
    }

    private static void AddSinkField(EmbedBuilder embed, IReadOnlyList<FrmResourceSink>? sink)
    {
        var s = sink?.FirstOrDefault();
        if (s is null) return;

        AddField(embed, "AWESOME Sink",
            $"{s.NumCoupon} coupon(s) · {s.TotalPoints:N0} points\nNext coupon at {s.PointsToCoupon:N0} ({s.Percent:0.#}%)");
    }

    /// <summary>
    /// Space-elevator progress, when there is one. The clan had none as of
    /// 2026-07-25 (phase 0), so this is usually absent — and
    /// <see cref="FrmSpaceElevator"/> is the one model still unverified against
    /// real data, hence the defensive null checks.
    /// </summary>
    private static void AddElevatorField(EmbedBuilder embed, IReadOnlyList<FrmSpaceElevator>? elevators)
    {
        var e = elevators?.FirstOrDefault();
        if (e is null) return;

        if (e.CurrentPhase is null || e.CurrentPhase.Count == 0)
        {
            AddField(embed, "Space elevator", e.FullyUpgraded ? "Fully upgraded" : "No phase in progress");
            return;
        }

        var lines = e.CurrentPhase
            .OrderByDescending(i => i.TotalCost == 0 ? 0 : (double)i.RemainingCost / i.TotalCost)
            .Take(5)
            .Select(i =>
            {
                var done = i.TotalCost - i.RemainingCost;
                var pct = i.TotalCost == 0 ? 100 : (int)(100.0 * done / i.TotalCost);
                return $"• {Escape(i.Name)} — {done:N0}/{i.TotalCost:N0} ({pct}%)";
            });

        AddField(embed, "Space elevator phase", string.Join("\n", lines));
    }

    /// <summary>
    /// Who's been on lately, for the on-demand report only — the daily digest
    /// has an exact one-day figure and doesn't need a rolling approximation.
    ///
    /// Approximation: a session counts if it STARTED inside the window, so a
    /// marathon that began before the cutoff is excluded rather than
    /// part-counted. Good enough for a summary, and it never double-counts.
    /// </summary>
    private async Task AddRecentPlayersFieldAsync(EmbedBuilder embed, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var cutoff = DateTime.UtcNow - PlayerWindow;
        var recent = await db.SatisfactorySessions
            .Where(s => s.StartedUtc >= cutoff)
            .ToListAsync(ct);

        if (recent.Count == 0) return;

        var lines = recent
            .GroupBy(s => s.PlayerName, StringComparer.Ordinal)
            .Select(g => new { Name = g.Key, Total = g.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.Duration) })
            .OrderByDescending(p => p.Total)
            .Take(5)
            .Select(p => $"• **{Escape(p.Name)}** — {SatisfactoryPresenceService.Humanize(p.Total)}");

        AddField(embed, "Most active (7 days)", string.Join("\n", lines));
    }

    /// <summary>
    /// Adds a field, truncating the value to Discord's per-field limit.
    ///
    /// <para><b>This is a throw guard, not cosmetics.</b> EmbedFieldBuilder
    /// rejects a value over 1024 characters with an ArgumentException, which
    /// would take down the ENTIRE digest — not just the offending field. Most
    /// sections here are bounded by construction (5 production rows, 12
    /// unlocks), but the playtime list is 10 rows of player-chosen names, and
    /// <see cref="Escape"/> can nearly double a name's length by backslashing
    /// its markdown. That's the one section that can realistically overflow, and
    /// a silent truncation beats a missing post.</para>
    /// </summary>
    private static void AddField(EmbedBuilder embed, string name, string value)
    {
        const int limit = 1024;
        const string ellipsis = "\n…";

        if (value.Length > limit)
        {
            var cut = limit - ellipsis.Length;

            // Don't slice a surrogate pair in half. A name ending in an emoji
            // can put the cut between the two halves of one character, and a
            // lone surrogate is not valid UTF-8 — Discord rejects the payload,
            // which is the exact failure this method exists to prevent.
            if (char.IsHighSurrogate(value[cut - 1])) cut--;

            value = value[..cut] + ellipsis;
        }

        embed.AddField(name, value, false);
    }

    /// <summary>
    /// An inline field whose value came from the API and may therefore be null
    /// or blank, which EmbedFieldBuilder rejects outright.
    /// </summary>
    private static void AddInlineField(EmbedBuilder embed, string name, string? value) =>
        embed.AddField(name, string.IsNullOrWhiteSpace(value) ? "unknown" : value, true);

    /// <summary>
    /// The game API returns GamePhase as a full Unreal object path:
    ///
    /// <code>/Script/FactoryGame.FGGamePhase'/Game/FactoryGame/GamePhases/GP_Project_Assembly_Phase_1.GP_Project_Assembly_Phase_1'</code>
    ///
    /// The readable part is the asset name after the last dot. Strips the
    /// <c>GP_</c> prefix and splits the underscores, so the above becomes
    /// "Project Assembly Phase 1". Falls back to the raw value rather than
    /// blanking, since a wrong-looking label beats a missing one.
    /// </summary>
    private static string PhaseName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var s = raw.Trim().TrimEnd('\'');

        var dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];

        if (s.StartsWith("GP_", StringComparison.OrdinalIgnoreCase)) s = s[3..];

        s = s.Replace('_', ' ').Trim();

        return s.Length == 0 ? raw : s;
    }

    /// <summary>Embed titles throw above 256 characters.</summary>
    private static string Title(string s) =>
        s.Length <= 256 ? s : s[..255] + "…";

    /// <summary>
    /// Session, item and player names are not bot-controlled text, and they land
    /// in a Discord message. Neutralize markdown so they can't forge formatting
    /// or a mass-ping.
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
