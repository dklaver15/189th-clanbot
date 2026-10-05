using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>
/// Draws the clan's rail network as a PNG: the track, the stations, and where
/// every train is right now.
///
/// ── Why the track is cached and the trains are not ──
/// getTrainRails is the heaviest endpoint this bot touches. It returns one
/// object per rail segment, each carrying a dense spline, so a mature network is
/// megabytes. It also only changes when somebody builds. Trains move constantly
/// and getTrains is tiny. So the geometry is held for
/// <see cref="BotConfig.SatisfactoryRailMapCacheMinutes"/> and only the markers
/// are re-read, which turns a repeat render into two small requests.
///
/// <para>That cache is shared: <see cref="SatisfactoryFactoryMapRenderer"/> and
/// the daily digest both read through this class rather than pulling the
/// heaviest response in the codebase a second time.</para>
///
/// ── Everything else lives in SatisfactoryMapCanvas ──
/// The projection, the label placer and the chrome are shared with the factory
/// map. Two maps of the same world with two label placers that disagree about
/// crowding is a guarantee that fixing one silently leaves the other broken.
///
/// ── Failure handling ──
/// Returns null on anything going wrong, already logged. A map is never worth
/// losing the message it was attached to.
/// </summary>
public sealed class SatisfactoryRailMapRenderer
{
    private readonly FrmApiService _frm;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryRailMapRenderer> _logger;

    /// <summary>
    /// Kept as a forwarding constant because callers outside the map code (the
    /// digest, the render harness) reason in metres and should not have to know
    /// which class owns the projection.
    /// </summary>
    public const double UnitsPerMetre = SatisfactoryMapCanvas.UnitsPerMetre;

    private const string RailHex     = "8fa3c8";
    private const string StationHex  = "ffd86b";
    private const string MovingHex   = "00d4ff";

    /// <summary>
    /// Orange, not the amber the stopped-train ALERT embed uses. On the map a
    /// stopped train sits next to a gold station marker, and the two ambers were
    /// indistinguishable at marker size: an early render had a legend whose
    /// "Station" and "Stopped" swatches looked identical. The alert embed has no
    /// such neighbour and keeps its amber.
    /// </summary>
    private const string StoppedHex  = "e67e22";

    private const string DerailedHex = "ed4245";

    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private IReadOnlyList<FrmRailSegment>? _cachedRails;
    private DateTime _railsFetchedUtc = DateTime.MinValue;

    public SatisfactoryRailMapRenderer(
        FrmApiService frm,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryRailMapRenderer> logger)
    {
        _frm = frm;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan RailCacheFor =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryRailMapCacheMinutes, 1, 1440));

    /// <summary>
    /// Renders the map, or says why it couldn't.
    ///
    /// <para>Same reasoning as the factory map: "no track built yet" and "the
    /// game server is offline" are opposite situations for whoever typed the
    /// command, and collapsing both into one null meant they got one sentence
    /// covering both. See <see cref="MapOutcome"/>.</para>
    /// </summary>
    public async Task<MapRender> TryRenderAsync(string subtitle, CancellationToken ct = default)
    {
        try
        {
            var rails = await GetCachedRailsAsync(ct);

            // Stations and trains are cheap and always fresh. Both are optional:
            // a network under construction has track and no trains, and that is
            // still a map worth looking at.
            var stationRead = await _frm.GetTrainStationsWithStatusAsync(ct);
            var trainRead = await _frm.GetTrainsWithStatusAsync(ct);

            var stations = stationRead.Data ?? Array.Empty<FrmTrainStation>();
            var trains = trainRead.Data ?? Array.Empty<FrmTrain>();

            var polylines = (rails ?? Array.Empty<FrmRailSegment>())
                .Select(SatisfactoryRail.Points)
                .Where(p => p.Count >= 2)
                .ToList();

            if (polylines.Count == 0 && stations.Count == 0)
            {
                // The verdict rests on the STATION read alone, and deliberately
                // so. Emptiness here means "no track and no stations", and of the
                // three reads only that one both went to the server just now and
                // feeds the test: getTrainRails comes from a cache that hands
                // back a stale copy during an outage without saying it did, and
                // getTrains isn't part of the test at all — an FRM update that
                // changed only the train shape would otherwise have us announce
                // a mod problem to somebody whose real situation is that nobody
                // has laid any track yet.
                var why = stationRead.Status switch
                {
                    // Answered, and said there are none. The only definitive case.
                    FrmApiService.FrmReadStatus.Ok => MapOutcome.Empty,

                    // Answered with something we can't read: the server is up and
                    // restarting it would fix nothing.
                    FrmApiService.FrmReadStatus.Unparseable => MapOutcome.Unreadable,

                    // Can't happen while no rail endpoint is capped, and listed
                    // anyway: letting it fall into the catch-all below would
                    // report a healthy server that merely sent too much as an
                    // outage, which is the whole failure this change removes.
                    FrmApiService.FrmReadStatus.TooLarge => MapOutcome.TooLarge,

                    // Didn't answer. We have no idea whether anything is built, so
                    // the honest report is the outage, not an empty world.
                    _ => MapOutcome.Unreachable,
                };

                // Information, not Debug: production logs at Information, so the
                // old Debug line was never written anywhere anybody could read it.
                _logger.LogInformation(
                    "Satisfactory rail map drew nothing ({Why}). getTrainStation={Stations}, getTrains={Trains}",
                    why, stationRead.Status, trainRead.Status);

                return MapRender.No(why);
            }

            ct.ThrowIfCancellationRequested();

            return MapRender.Ok(Render(polylines, stations, trains, subtitle));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render the Satisfactory rail map");
            return MapRender.No(MapOutcome.Failed);
        }
    }

    /// <summary>
    /// Total built track, in metres, from the CACHED geometry. Null when nothing
    /// is cached. Deliberately never fetches: the daily digest reads this, and a
    /// morning report must not pull the heaviest endpoint on its own account.
    /// </summary>
    public double? CachedTrackMetres()
    {
        var rails = _cachedRails;
        if (rails is null || rails.Count == 0) return null;

        return rails.Sum(SatisfactoryRail.LengthUnits) / UnitsPerMetre;
    }

    /// <summary>
    /// The cached track geometry, refetched when stale. Public so the factory
    /// map can draw rail without a second copy of the biggest response FRM
    /// serves.
    ///
    /// <para>Guarded by a semaphore rather than a lock because the fetch is
    /// async. Without it, two commands issued together would both miss the cache
    /// and both pull that response at the same moment, which is exactly the load
    /// this cache exists to prevent.</para>
    /// </summary>
    public async Task<IReadOnlyList<FrmRailSegment>?> GetCachedRailsAsync(CancellationToken ct = default)
    {
        if (_cachedRails is not null && DateTime.UtcNow - _railsFetchedUtc < RailCacheFor)
            return _cachedRails;

        await _cacheGate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: whoever we queued behind has probably
            // just filled it.
            if (_cachedRails is not null && DateTime.UtcNow - _railsFetchedUtc < RailCacheFor)
                return _cachedRails;

            var fetched = await _frm.GetTrainRailsAsync(ct);

            if (fetched is null)
            {
                // Keep whatever we already had. Stale track beats no map, and
                // the geometry barely changes: a server blip should not turn a
                // working map into an error message.
                _logger.LogDebug("getTrainRails was unreachable; keeping the cached geometry");
                return _cachedRails;
            }

            _cachedRails = fetched;
            _railsFetchedUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Cached {Count} Satisfactory rail segment(s) for the map, {Metres:0} m of track",
                fetched.Count, fetched.Sum(SatisfactoryRail.LengthUnits) / UnitsPerMetre);

            return _cachedRails;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    // ─── Drawing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Static on purpose: the whole draw is a pure function of the three lists,
    /// which is what lets a render harness call it with synthetic geometry and
    /// look at the result without a live server to fetch from.
    /// </summary>
    private static byte[] Render(
        IReadOnlyList<IReadOnlyList<FrmLocation>> polylines,
        IReadOnlyList<FrmTrainStation> stations,
        IReadOnlyList<FrmTrain> trains,
        string subtitle)
    {
        using var canvas = SatisfactoryMapCanvas.Fit(
            polylines.SelectMany(p => p)
                .Concat(stations.Where(s => s.Location is not null).Select(s => s.Location!))
                .Concat(trains.Where(t => t.Location is not null).Select(t => t.Location!)));

        DrawRails(canvas, polylines);
        DrawStations(canvas, stations);
        DrawTrains(canvas, trains);

        var legend = new List<SatisfactoryMapCanvas.LegendEntry>
        {
            new(StationHex, "Station", Square: true),
        };

        // Only states actually on the map get an entry. A permanent "Derailed"
        // swatch on a healthy network trains people to stop reading it.
        if (trains.Any(t => !t.Derailed && !SatisfactoryRail.IsStationary(t)))
            legend.Add(new(MovingHex, "Moving"));

        if (trains.Any(t => !t.Derailed && SatisfactoryRail.IsStationary(t)))
            legend.Add(new(StoppedHex, "Stopped"));

        if (trains.Any(t => t.Derailed))
            legend.Add(new(DerailedHex, "Derailed"));

        return canvas.Finish("Rail network", subtitle, legend);
    }

    /// <summary>
    /// Track, drawn twice: a wide dim stroke under a narrow bright one.
    ///
    /// <para>The under-stroke is what stops a dense yard reading as a solid
    /// blob. Parallel lines a few pixels apart merge at this scale, and the halo
    /// gives the eye an edge to separate them by.</para>
    /// </summary>
    private static void DrawRails(
        SatisfactoryMapCanvas canvas, IReadOnlyList<IReadOnlyList<FrmLocation>> polylines)
    {
        using var halo = new SKPaint
        {
            Color = SatisfactoryMapCanvas.Hex(RailHex, 40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        using var line = new SKPaint
        {
            Color = SatisfactoryMapCanvas.Hex(RailHex, 205),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        using var path = new SKPath();

        foreach (var points in polylines)
        {
            path.MoveTo(canvas.ToPixel(points[0]));
            for (var i = 1; i < points.Count; i++) path.LineTo(canvas.ToPixel(points[i]));
        }

        canvas.Canvas.DrawPath(path, halo);
        canvas.Canvas.DrawPath(path, line);
    }

    private static void DrawStations(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmTrainStation> stations)
    {
        using var fill = new SKPaint { Color = SatisfactoryMapCanvas.Hex(StationHex, 230), IsAntialias = true };
        using var ring = new SKPaint
        {
            Color = SatisfactoryMapCanvas.Hex(StationHex, 90),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
        };

        foreach (var station in stations.Where(s => s.Location is not null))
        {
            var p = canvas.ToPixel(station.Location!);

            canvas.Canvas.DrawCircle(p, 8f, ring);
            canvas.Canvas.DrawRect(new SKRect(p.X - 3.5f, p.Y - 3.5f, p.X + 3.5f, p.Y + 3.5f), fill);

            // The marker is an obstacle in its own right. Without this a label
            // pushed clear of other TEXT can still land on a neighbouring
            // station's dot, which reads as a mislabelled station.
            canvas.AddObstacle(new SKRect(p.X - 9f, p.Y - 9f, p.X + 9f, p.Y + 9f));

            canvas.AddLabel(p, SatisfactoryRail.DisplayName(station), StationHex,
                SatisfactoryMapCanvas.PriorityHigh);
        }
    }

    /// <summary>
    /// Trains as a coloured dot with a heading tick.
    ///
    /// <para>The tick is worth the few lines: on a static image a dot says where
    /// a train is and nothing else, and "which way is it going" is most of what
    /// somebody looking at a rail map wants to know.</para>
    /// </summary>
    private static void DrawTrains(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmTrain> trains)
    {
        foreach (var train in trains.Where(t => t.Location is not null))
        {
            var p = canvas.ToPixel(train.Location!);

            var hex = train.Derailed ? DerailedHex
                : SatisfactoryRail.IsStationary(train) ? StoppedHex
                : MovingHex;

            using (var glow = new SKPaint { Color = SatisfactoryMapCanvas.Hex(hex, 55), IsAntialias = true })
                canvas.Canvas.DrawCircle(p, 11f, glow);

            using (var dot = new SKPaint { Color = SatisfactoryMapCanvas.Hex(hex, 245), IsAntialias = true })
                canvas.Canvas.DrawCircle(p, 5.5f, dot);

            // Rotation is degrees clockwise from north, so it converts to screen
            // space as sin for x and negative cos for y.
            if (!SatisfactoryRail.IsStationary(train) && train.Location!.Rotation != 0)
            {
                var radians = train.Location.Rotation * Math.PI / 180.0;
                var tip = new SKPoint(
                    p.X + (float)(Math.Sin(radians) * 15.0),
                    p.Y - (float)(Math.Cos(radians) * 15.0));

                using var tick = new SKPaint
                {
                    Color = SatisfactoryMapCanvas.Hex(hex, 200),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.4f,
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true,
                };

                canvas.Canvas.DrawLine(p, tip, tick);
            }

            canvas.AddObstacle(new SKRect(p.X - 12f, p.Y - 12f, p.X + 12f, p.Y + 12f));

            // A derailed or stopped train outranks a station name for the last
            // free slot in a crowded yard: the station is on the map every day
            // and the stopped train is the reason somebody opened it.
            var priority = train.Derailed || SatisfactoryRail.IsStationary(train)
                ? SatisfactoryMapCanvas.PriorityUrgent
                : SatisfactoryMapCanvas.PriorityNormal;

            canvas.AddLabel(p, SatisfactoryRail.DisplayName(train), hex, priority);
        }
    }
}
