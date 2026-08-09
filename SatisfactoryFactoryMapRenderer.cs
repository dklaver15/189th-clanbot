using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace ClanGuardBot.Services;

/// <summary>
/// Which parts of the world to draw. Flags rather than an enum of presets so
/// the command can offer readable combinations without the renderer knowing
/// what a "preset" is.
/// </summary>
[Flags]
public enum FactoryMapLayers
{
    None = 0,
    Rail = 1,
    Belts = 2,
    Pipes = 4,
    Production = 8,
    Power = 16,
    Extraction = 32,
    Nodes = 64,

    Logistics = Rail | Belts | Pipes,

    /// <summary>
    /// Everything except resource nodes. Nodes are world generation, hundreds
    /// of them, most of them nowhere near the base, and including them in the
    /// default view drags the fitted bounds out to the whole map and shrinks the
    /// factory to a smudge. They are worth a layer, not a default.
    /// </summary>
    Everything = Rail | Belts | Pipes | Production | Power | Extraction,
}

/// <summary>
/// Draws the clan's factory: belts, pipes, machines coloured by what they are
/// doing, generators, extractors, and optionally every resource node.
///
/// ── Two framings, and why both are needed ──
/// A constructor is 8 m. The clan's rail network is 1.7 km across. At a fitted
/// whole-base zoom an individual machine is well under a pixel, so an overview
/// can only ever show SHAPE: where the belt web runs, where the dense blocks
/// are. Focus mode frames a few hundred metres around a named thing and draws
/// real footprints from each building's BoundingBox, which is the only view
/// that can answer "which machine is idle". Neither replaces the other.
///
/// ── Caching, in three tiers ──
/// Not one TTL, because the three kinds of data age at completely different
/// rates and the endpoints are expensive enough that guessing wrong is costly:
///   • Machine STATE (getFactory, getExtractor) is most of the point and
///     changes constantly. Short TTL.
///   • Belt and pipe GEOMETRY changes only when somebody builds. Long TTL.
///   • Resource nodes are world generation. Cached for the life of the process.
/// Rail geometry is not cached here at all; it is borrowed from
/// <see cref="SatisfactoryRailMapRenderer"/>, which already holds the single
/// heaviest response in the codebase and should not hold it twice.
///
/// ── Failure handling ──
/// Returns null on anything going wrong, already logged. Individual layers fail
/// independently: a map missing its pipes still beats an error message.
/// </summary>
public sealed class SatisfactoryFactoryMapRenderer
{
    private readonly FrmApiService _frm;
    private readonly SatisfactoryRailMapRenderer _rail;
    private readonly BotConfig _config;
    private readonly ILogger<SatisfactoryFactoryMapRenderer> _logger;

    // ── Palette ──────────────────────────────────────────────────────────────
    private const string BeltHex        = "5865f2";   // blurple
    private const string PipeHex        = "00a3c4";   // teal
    private const string RailHex        = "8fa3c8";
    private const string ProducingHex   = "3ba55d";
    private const string IdleHex        = "f1c40f";
    private const string PausedHex      = "7a869a";
    private const string UnsetHex       = "c94f7c";
    private const string GeneratorHex   = "ffd86b";
    private const string ExtractorHex   = "e67e22";
    private const string StationHex     = "ffd86b";
    private const string NodeFreeHex    = "7be39b";
    private const string NodeUsedHex    = "55627a";

    /// <summary>
    /// A building's footprint is drawn as a real rectangle once it would cover
    /// at least this many pixels on its short side. Below it, a rectangle is
    /// indistinguishable from a dot and costs far more to draw, so the map falls
    /// back to dots. This is the numeric form of "an overview cannot show
    /// individual machines".
    /// </summary>
    private const float MinFootprintPixels = 3f;

    /// <summary>
    /// Above this many buildings in view, only the ones that are NOT producing
    /// get named.
    ///
    /// <para>The cap applies to healthy machines only, and that asymmetry is the
    /// point. Naming twenty working constructors "Iron Plate" is noise that
    /// spends the label budget and pushes out the one that stopped. A machine
    /// that is idle, paused or has no recipe is labelled at any zoom and any
    /// count, because that is the thing somebody opened the map to find.</para>
    /// </summary>
    private const int MaxBuildingLabels = 24;

    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<FrmBuilding>? _buildings;
    private DateTime _buildingsAt = DateTime.MinValue;

    private IReadOnlyList<FrmExtractor>? _extractors;
    private DateTime _extractorsAt = DateTime.MinValue;

    private IReadOnlyList<FrmConveyor>? _belts;
    private DateTime _beltsAt = DateTime.MinValue;

    private IReadOnlyList<FrmConveyor>? _pipes;
    private DateTime _pipesAt = DateTime.MinValue;

    /// <summary>Never expires. Nodes are world generation, they do not move.</summary>
    private IReadOnlyList<FrmResourceNode>? _nodes;

    public SatisfactoryFactoryMapRenderer(
        FrmApiService frm,
        SatisfactoryRailMapRenderer rail,
        IOptions<BotConfig> config,
        ILogger<SatisfactoryFactoryMapRenderer> logger)
    {
        _frm = frm;
        _rail = rail;
        _config = config.Value;
        _logger = logger;
    }

    private TimeSpan StateCacheFor =>
        TimeSpan.FromMinutes(Math.Clamp(_config.SatisfactoryFactoryMapCacheMinutes, 1, 1440));

    /// <summary>Geometry outlives state by 4x. Belts get built far less often than machines idle.</summary>
    private TimeSpan GeometryCacheFor => StateCacheFor * 4;

    /// <summary>
    /// Renders the map.
    /// </summary>
    /// <param name="focus">
    /// Name (or part of one) of a station, building, extractor or generator to
    /// centre on. Null renders the whole base fitted to its own extent.
    /// </param>
    /// <param name="radiusMetres">Half-width of a focused view. Ignored without a focus.</param>
    /// <returns>PNG bytes, or null when there is nothing to draw.</returns>
    public async Task<byte[]?> TryRenderAsync(
        string? focus,
        double? radiusMetres,
        FactoryMapLayers layers,
        CancellationToken ct = default)
    {
        try
        {
            var data = await LoadAsync(layers, ct);

            if (data.IsEmpty)
            {
                _logger.LogDebug("Skipping the Satisfactory factory map: nothing came back to draw");
                return null;
            }

            ct.ThrowIfCancellationRequested();

            FrmLocation? centre = null;
            string? focusName = null;

            if (!string.IsNullOrWhiteSpace(focus))
            {
                (centre, focusName) = Resolve(focus!, data);

                if (centre is null)
                {
                    _logger.LogDebug("Satisfactory factory map: nothing matched focus \"{Focus}\"", focus);
                    return null;
                }
            }

            var radius = Math.Clamp(
                radiusMetres ?? _config.SatisfactoryMapFocusRadiusMetres, 10, 5000);

            using var canvas = centre is null
                ? SatisfactoryMapCanvas.Fit(Extent(data))
                : SatisfactoryMapCanvas.Focus(centre, radius);

            return Draw(canvas, data, layers, focusName, radius);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render the Satisfactory factory map");
            return null;
        }
    }

    // ─── Data ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything one render needs, already fetched. Public so the render
    /// harness can build a synthetic world and exercise the draw path without a
    /// live server, the same reason the rail renderer's Render is static.
    /// </summary>
    public sealed record MapData(
        IReadOnlyList<FrmRailSegment> Rails,
        IReadOnlyList<FrmTrainStation> Stations,
        IReadOnlyList<FrmConveyor> Belts,
        IReadOnlyList<FrmConveyor> Pipes,
        IReadOnlyList<FrmBuilding> Buildings,
        IReadOnlyList<FrmGenerator> Generators,
        IReadOnlyList<FrmExtractor> Extractors,
        IReadOnlyList<FrmResourceNode> Nodes)
    {
        public bool IsEmpty =>
            Rails.Count == 0 && Stations.Count == 0 && Belts.Count == 0 && Pipes.Count == 0
            && Buildings.Count == 0 && Generators.Count == 0 && Extractors.Count == 0
            && Nodes.Count == 0;
    }

    /// <summary>
    /// Fetches every requested layer, serialised behind one gate.
    ///
    /// <para>The gate is not about thread safety on the fields; it is about the
    /// server. Two people running the command together would otherwise both
    /// miss the cache and both pull getFactory and getBelts at the same moment,
    /// which is precisely the load these caches exist to prevent.</para>
    /// </summary>
    private async Task<MapData> LoadAsync(FactoryMapLayers layers, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            // Stations are always fetched, whatever the layers. They are the
            // only named landmarks on the map, they are few, and an overview
            // with no labels at all is impossible to orient in.
            var stations = await _frm.GetTrainStationsAsync(ct) ?? Array.Empty<FrmTrainStation>();

            var rails = layers.HasFlag(FactoryMapLayers.Rail)
                ? await _rail.GetCachedRailsAsync(ct) ?? Array.Empty<FrmRailSegment>()
                : Array.Empty<FrmRailSegment>();

            var belts = layers.HasFlag(FactoryMapLayers.Belts)
                ? await CachedAsync(() => _belts, v => _belts = v, () => _beltsAt, t => _beltsAt = t,
                    GeometryCacheFor, now, "getBelts", () => _frm.GetBeltsAsync(ct))
                : Array.Empty<FrmConveyor>();

            var pipes = layers.HasFlag(FactoryMapLayers.Pipes)
                ? await CachedAsync(() => _pipes, v => _pipes = v, () => _pipesAt, t => _pipesAt = t,
                    GeometryCacheFor, now, "getPipes", () => _frm.GetPipesAsync(ct))
                : Array.Empty<FrmConveyor>();

            var buildings = layers.HasFlag(FactoryMapLayers.Production)
                ? await CachedAsync(() => _buildings, v => _buildings = v, () => _buildingsAt, t => _buildingsAt = t,
                    StateCacheFor, now, "getFactory", () => _frm.GetFactoryAsync(ct))
                : Array.Empty<FrmBuilding>();

            var extractors = layers.HasFlag(FactoryMapLayers.Extraction)
                ? await CachedAsync(() => _extractors, v => _extractors = v, () => _extractorsAt, t => _extractorsAt = t,
                    StateCacheFor, now, "getExtractor", () => _frm.GetExtractorsAsync(ct))
                : Array.Empty<FrmExtractor>();

            // Generators are not cached: getGenerators is already polled by the
            // alert path, it is small, and fuel state is the whole reason to
            // look at one.
            var generators = layers.HasFlag(FactoryMapLayers.Power)
                ? await _frm.GetGeneratorsAsync(ct) ?? Array.Empty<FrmGenerator>()
                : Array.Empty<FrmGenerator>();

            var nodes = Array.Empty<FrmResourceNode>() as IReadOnlyList<FrmResourceNode>;

            if (layers.HasFlag(FactoryMapLayers.Nodes))
            {
                _nodes ??= await _frm.GetResourceNodesAsync(ct);
                nodes = _nodes ?? Array.Empty<FrmResourceNode>();
            }

            return new MapData(rails, stations, belts, pipes, buildings, generators, extractors, nodes);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Read-through cache for one layer.
    ///
    /// <para>A failed fetch KEEPS the previous value rather than clearing it.
    /// Stale geometry beats no map, and a server blip should not turn a working
    /// command into an error for the length of the outage.</para>
    /// </summary>
    private async Task<IReadOnlyList<T>> CachedAsync<T>(
        Func<IReadOnlyList<T>?> get,
        Action<IReadOnlyList<T>?> set,
        Func<DateTime> getStamp,
        Action<DateTime> setStamp,
        TimeSpan ttl,
        DateTime now,
        string endpoint,
        Func<Task<IReadOnlyList<T>?>> fetch)
    {
        var cached = get();
        if (cached is not null && now - getStamp() < ttl) return cached;

        var fetched = await fetch();

        if (fetched is null)
        {
            _logger.LogDebug("FRM {Endpoint} was unreachable; keeping the cached copy", endpoint);
            return cached ?? Array.Empty<T>();
        }

        set(fetched);
        setStamp(now);

        _logger.LogInformation("Cached {Count} entries from FRM {Endpoint} for the factory map",
            fetched.Count, endpoint);

        return fetched;
    }

    /// <summary>
    /// Every point the fitted view must contain.
    ///
    /// <para>Belt and pipe SPLINES are deliberately excluded and only their
    /// endpoints used. A spline is hundreds of points and the extent needs the
    /// shape of the base, not every vertex; walking them all to compute a
    /// bounding box that two endpoints already imply is pure cost.</para>
    /// </summary>
    private static IEnumerable<FrmLocation> Extent(MapData d)
    {
        foreach (var r in d.Rails)
            foreach (var p in SatisfactoryRail.Points(r)) yield return p;

        foreach (var s in d.Stations) if (s.Location is not null) yield return s.Location;

        foreach (var c in d.Belts.Concat(d.Pipes))
        {
            if (c.Location0 is not null) yield return c.Location0;
            if (c.Location1 is not null) yield return c.Location1;
        }

        foreach (var b in d.Buildings) if (b.Location is not null) yield return b.Location;
        foreach (var g in d.Generators) if (g.Location is not null) yield return g.Location;
        foreach (var e in d.Extractors) if (e.Location is not null) yield return e.Location;
        foreach (var n in d.Nodes) if (n.Location is not null) yield return n.Location;
    }

    /// <summary>
    /// Finds what to centre on. Stations first, because they are the things
    /// people have actually named; then extractors and buildings, which mostly
    /// carry generic type names like "Constructor" and would otherwise swallow
    /// a query meant for a station.
    /// </summary>
    private static (FrmLocation? Where, string? What) Resolve(string query, MapData d)
    {
        var q = query.Trim();

        foreach (var s in d.Stations)
            if (s.Location is not null && Matches(s.Name, q)) return (s.Location, SatisfactoryRail.DisplayName(s));

        foreach (var e in d.Extractors)
            if (e.Location is not null && Matches(e.Name, q)) return (e.Location, e.Name);

        foreach (var g in d.Generators)
            if (g.Location is not null && Matches(g.Name, q)) return (g.Location, g.Name);

        foreach (var b in d.Buildings)
            if (b.Location is not null && (Matches(b.Name, q) || Matches(b.Recipe, q)))
                return (b.Location, string.IsNullOrWhiteSpace(b.Recipe) ? b.Name : $"{b.Name} ({b.Recipe})");

        return (null, null);
    }

    private static bool Matches(string? candidate, string query) =>
        !string.IsNullOrWhiteSpace(candidate)
        && candidate.Contains(query, StringComparison.OrdinalIgnoreCase);

    // ─── Drawing ─────────────────────────────────────────────────────────────

    private static byte[] Draw(
        SatisfactoryMapCanvas canvas,
        MapData d,
        FactoryMapLayers layers,
        string? focusName,
        double radiusMetres)
    {
        // Back to front, cheapest and least important first. Nodes underneath
        // everything, machines on top, so a building is never hidden by the
        // belt feeding it.
        var nodes = DrawNodes(canvas, d.Nodes);
        var rails = DrawSplines(canvas, d.Rails.Select(SatisfactoryRail.Points), RailHex, 1.8f, 0.16f);
        var pipes = DrawSplines(canvas, d.Pipes.Select(Points), PipeHex, 1.6f, 0.14f);
        var belts = DrawSplines(canvas, d.Belts.Select(Points), BeltHex, 1.6f, 0.16f);

        var counts = DrawBuildings(canvas, d.Buildings);
        var generators = DrawGenerators(canvas, d.Generators);
        var extractors = DrawExtractors(canvas, d.Extractors);
        var stations = DrawStations(canvas, d.Stations);

        // Legend built from what was actually DRAWN, not from what was fetched.
        // A focused view holds the whole world in memory and shows a corner of
        // it; listing "Rail" for track that is two kilometres off-screen sends
        // somebody hunting the image for a line that isn't there.
        var legend = BuildLegend(counts, layers, stations, belts, pipes, rails, generators, extractors, nodes);

        var title = focusName is null ? "Factory" : $"Factory: {Truncate(focusName, 48)}";

        var subtitle = focusName is null
            ? "189th Clanguard  ·  whole base"
            : $"189th Clanguard  ·  {radiusMetres:0} m around this point";

        var footnote = Summary(counts, belts, pipes, rails, generators, extractors, nodes, layers);

        return canvas.Finish(title, subtitle, legend, footnote);
    }

    private static IReadOnlyList<FrmLocation> Points(FrmConveyor c)
    {
        if (c.SplineData is { Count: >= 2 }) return c.SplineData;

        var ends = new List<FrmLocation>(2);
        if (c.Location0 is not null) ends.Add(c.Location0);
        if (c.Location1 is not null) ends.Add(c.Location1);

        return ends.Count == 2 ? ends : Array.Empty<FrmLocation>();
    }

    /// <summary>
    /// Draws a set of polylines as one path: a wide dim halo under a narrow
    /// bright line.
    ///
    /// <para>The halo is what stops a dense belt bus reading as a solid slab.
    /// Parallel lines a few pixels apart merge at overview scale, and the halo
    /// gives the eye an edge to separate them by.</para>
    ///
    /// <para>Batched into ONE SKPath rather than a draw call per belt. A mature
    /// base is thousands of conveyors, and per-object paint churn there is the
    /// difference between a render and a timeout.</para>
    /// </summary>
    private static int DrawSplines(
        SatisfactoryMapCanvas canvas,
        IEnumerable<IReadOnlyList<FrmLocation>> lines,
        string hex,
        float strokeWidth,
        float haloAlpha)
    {
        using var path = new SKPath();
        var drawn = 0;
        var inView = 0;

        // Generous margin: a belt whose endpoints are both outside a focused
        // view can still run straight through the middle of it.
        var margin = canvas.UnitsPerPixel * SatisfactoryMapCanvas.Width;

        foreach (var points in lines)
        {
            if (points.Count < 2) continue;
            if (!points.Any(p => canvas.IsVisible(p, margin))) continue;

            path.MoveTo(canvas.ToPixel(points[0]));
            for (var i = 1; i < points.Count; i++) path.LineTo(canvas.ToPixel(points[i]));

            drawn++;

            // Counted separately and STRICTLY, with no margin, because this is
            // what the legend keys off. Something drawn only because the margin
            // let it through is off the edge of the image, and giving it a
            // legend entry sends somebody hunting for a line that isn't there.
            if (points.Any(p => canvas.IsVisible(p))) inView++;
        }

        if (drawn == 0) return 0;

        using (var halo = new SKPaint
        {
            Color = SatisfactoryMapCanvas.Hex(hex, (byte)(255 * haloAlpha)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth + 3.2f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        })
        {
            canvas.Canvas.DrawPath(path, halo);
        }

        using (var line = new SKPaint
        {
            Color = SatisfactoryMapCanvas.Hex(hex, 205),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        })
        {
            canvas.Canvas.DrawPath(path, line);
        }

        return inView;
    }

    public sealed record BuildingCounts(int Producing, int Idle, int Paused, int Unconfigured)
    {
        public int Total => Producing + Idle + Paused + Unconfigured;
    }

    /// <summary>
    /// Machines, coloured by what they are doing, as real footprints when the
    /// zoom allows and dots when it does not.
    ///
    /// <para>Batched by STATE rather than drawn one at a time: four paints for
    /// a thousand machines instead of a thousand. The four states are the whole
    /// diagnostic value of this layer, so they are also exactly the right
    /// batching key.</para>
    /// </summary>
    private static BuildingCounts DrawBuildings(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmBuilding> buildings)
    {
        var buckets = new Dictionary<string, SKPath>
        {
            [ProducingHex] = new(),
            [IdleHex] = new(),
            [PausedHex] = new(),
            [UnsetHex] = new(),
        };

        int producing = 0, idle = 0, paused = 0, unset = 0;

        var visible = new List<FrmBuilding>();

        foreach (var b in buildings)
        {
            if (b.Location is null || !canvas.IsVisible(b.Location)) continue;

            visible.Add(b);

            var hex = !b.IsConfigured ? UnsetHex
                : b.IsPaused ? PausedHex
                : b.IsProducing ? ProducingHex
                : IdleHex;

            if (hex == ProducingHex) producing++;
            else if (hex == IdleHex) idle++;
            else if (hex == PausedHex) paused++;
            else unset++;

            var p = canvas.ToPixel(b.Location);
            var box = Footprint(canvas, b.BoundingBox);

            if (box is { } rect && Math.Min(rect.Width, rect.Height) >= MinFootprintPixels)
                buckets[hex].AddRect(rect);
            else
                buckets[hex].AddCircle(p.X, p.Y, 2.6f);
        }

        foreach (var (hex, path) in buckets)
        {
            if (path.IsEmpty) continue;

            using (var fill = new SKPaint
            {
                Color = SatisfactoryMapCanvas.Hex(hex, 170),
                Style = SKPaintStyle.Fill,
                IsAntialias = true,
            })
            {
                canvas.Canvas.DrawPath(path, fill);
            }

            using (var edge = new SKPaint
            {
                Color = SatisfactoryMapCanvas.Hex(hex, 235),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.1f,
                IsAntialias = true,
            })
            {
                canvas.Canvas.DrawPath(path, edge);
            }

            path.Dispose();
        }

        // Healthy machines are named only when there are few enough for it to
        // be worth reading. Broken ones are named always: see MaxBuildingLabels.
        var nameHealthy = visible.Count <= MaxBuildingLabels;

        foreach (var b in visible)
        {
            var hex = !b.IsConfigured ? UnsetHex
                : b.IsPaused ? PausedHex
                : b.IsProducing ? ProducingHex
                : IdleHex;

            var healthy = hex == ProducingHex;
            if (healthy && !nameHealthy) continue;

            var p = canvas.ToPixel(b.Location!);
            canvas.AddObstacle(new SKRect(p.X - 8f, p.Y - 8f, p.X + 8f, p.Y + 8f));

            // The recipe is the useful name. "Constructor" tells nobody
            // anything; "Iron Plate" is what somebody is looking for. An
            // unconfigured machine has no recipe, and "Constructor" is then
            // exactly right, because the point is that it has no job.
            var text = string.IsNullOrWhiteSpace(b.Recipe) ? b.Name : b.Recipe;

            canvas.AddLabel(p, text ?? "", hex,
                healthy ? SatisfactoryMapCanvas.PriorityNormal : SatisfactoryMapCanvas.PriorityHigh);
        }

        return new BuildingCounts(producing, idle, paused, unset);
    }

    /// <summary>
    /// A building's footprint in pixels, or null when it has no usable box.
    ///
    /// <para>Absolute value on the extents because nothing guarantees min is
    /// smaller than max on every axis once rotation is baked in, and a negative
    /// width silently draws nothing.</para>
    /// </summary>
    private static SKRect? Footprint(SatisfactoryMapCanvas canvas, FrmBoundingBox? box)
    {
        if (box?.Min is null || box.Max is null) return null;

        var a = canvas.ToPixel(box.Min);
        var b = canvas.ToPixel(box.Max);

        var rect = new SKRect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        return rect.Width <= 0 || rect.Height <= 0 ? null : rect;
    }

    /// <summary>
    /// Generators, as gold diamonds so they read as a different KIND of thing
    /// from the round extractors and the square machines even in monochrome.
    ///
    /// <para>A generator that can't start is drawn amber and always labelled,
    /// whatever the zoom, for the same reason a stopped extractor is: it is a
    /// power failure in the making, and the fuse alerts only fire once the
    /// circuit has already gone down.</para>
    ///
    /// <para>Returns how many were drawn. Zero is the expected answer if the
    /// live getGenerators response has no location field, since the model
    /// treats it as optional; that shows up as a missing layer rather than a
    /// crash.</para>
    /// </summary>
    private static int DrawGenerators(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmGenerator> generators)
    {
        var drawn = 0;

        foreach (var g in generators)
        {
            if (g.Location is null || !canvas.IsVisible(g.Location)) continue;

            var p = canvas.ToPixel(g.Location);
            var stalled = !g.CanStart;
            var hex = stalled ? IdleHex : GeneratorHex;

            using var diamond = new SKPath();
            diamond.MoveTo(p.X, p.Y - 6f);
            diamond.LineTo(p.X + 6f, p.Y);
            diamond.LineTo(p.X, p.Y + 6f);
            diamond.LineTo(p.X - 6f, p.Y);
            diamond.Close();

            using (var glow = new SKPaint { Color = SatisfactoryMapCanvas.Hex(hex, 55), IsAntialias = true })
                canvas.Canvas.DrawCircle(p, 10f, glow);

            using (var fill = new SKPaint { Color = SatisfactoryMapCanvas.Hex(hex, 235), IsAntialias = true })
                canvas.Canvas.DrawPath(diamond, fill);

            drawn++;

            if (stalled)
            {
                canvas.AddObstacle(new SKRect(p.X - 10f, p.Y - 10f, p.X + 10f, p.Y + 10f));
                canvas.AddLabel(p, g.Name ?? "Generator", IdleHex, SatisfactoryMapCanvas.PriorityUrgent);
            }
        }

        return drawn;
    }

    private static int DrawExtractors(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmExtractor> extractors)
    {
        var drawn = 0;

        foreach (var e in extractors)
        {
            if (e.Location is null || !canvas.IsVisible(e.Location)) continue;

            var p = canvas.ToPixel(e.Location);
            var stopped = !e.IsProducing || e.IsPaused;

            using (var glow = new SKPaint
            {
                Color = SatisfactoryMapCanvas.Hex(stopped ? IdleHex : ExtractorHex, 60),
                IsAntialias = true,
            })
            {
                canvas.Canvas.DrawCircle(p, 9f, glow);
            }

            using (var dot = new SKPaint
            {
                Color = SatisfactoryMapCanvas.Hex(stopped ? IdleHex : ExtractorHex, 240),
                IsAntialias = true,
            })
            {
                canvas.Canvas.DrawCircle(p, 4.5f, dot);
            }

            drawn++;

            // Only stopped extractors get named, at any zoom. A miner that has
            // stopped starves an entire production line, which makes it worth a
            // label even on an overview where nothing else is labelled.
            if (stopped)
            {
                canvas.AddObstacle(new SKRect(p.X - 10f, p.Y - 10f, p.X + 10f, p.Y + 10f));
                canvas.AddLabel(p, e.Recipe ?? e.Name ?? "Extractor", IdleHex,
                    SatisfactoryMapCanvas.PriorityUrgent);
            }
        }

        return drawn;
    }

    private static int DrawStations(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmTrainStation> stations)
    {
        var drawn = 0;

        using var fill = new SKPaint { Color = SatisfactoryMapCanvas.Hex(StationHex, 235), IsAntialias = true };
        using var ring = new SKPaint
        {
            Color = SatisfactoryMapCanvas.Hex(StationHex, 95),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
        };

        foreach (var s in stations)
        {
            if (s.Location is null || !canvas.IsVisible(s.Location)) continue;

            var p = canvas.ToPixel(s.Location);

            canvas.Canvas.DrawCircle(p, 8f, ring);
            canvas.Canvas.DrawRect(new SKRect(p.X - 3.5f, p.Y - 3.5f, p.X + 3.5f, p.Y + 3.5f), fill);

            canvas.AddObstacle(new SKRect(p.X - 9f, p.Y - 9f, p.X + 9f, p.Y + 9f));

            // Stations always get a label, at any zoom, whatever the layers.
            // They are the only named landmarks the world has, and an unlabelled
            // overview is impossible to orient in.
            canvas.AddLabel(p, SatisfactoryRail.DisplayName(s), StationHex,
                SatisfactoryMapCanvas.PriorityHigh);

            drawn++;
        }

        return drawn;
    }

    private static int DrawNodes(SatisfactoryMapCanvas canvas, IReadOnlyList<FrmResourceNode> nodes)
    {
        var free = 0;

        foreach (var n in nodes)
        {
            if (n.Location is null || !canvas.IsVisible(n.Location)) continue;

            var p = canvas.ToPixel(n.Location);

            // Exploited nodes are drawn dim and small. They are context, not
            // information: the whole value of this layer is spotting what has
            // NOT been built on yet.
            var hex = n.Exploited ? NodeUsedHex : NodeFreeHex;
            var radius = n.Exploited ? 2f : 3.4f;

            using var paint = new SKPaint
            {
                Color = SatisfactoryMapCanvas.Hex(hex, n.Exploited ? (byte)120 : (byte)215),
                IsAntialias = true,
            };

            canvas.Canvas.DrawCircle(p, radius, paint);

            if (!n.Exploited) free++;
        }

        return free;
    }

    // ─── Chrome ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Only states actually present get a legend entry. A permanent "Paused"
    /// swatch on a healthy factory teaches people to stop reading it.
    /// </summary>
    private static List<SatisfactoryMapCanvas.LegendEntry> BuildLegend(
        BuildingCounts c, FactoryMapLayers layers,
        int stations, int belts, int pipes, int rails, int generators, int extractors, int freeNodes)
    {
        var legend = new List<SatisfactoryMapCanvas.LegendEntry>();

        if (stations > 0) legend.Add(new(StationHex, "Station", Square: true));
        if (belts > 0) legend.Add(new(BeltHex, "Belt"));
        if (pipes > 0) legend.Add(new(PipeHex, "Pipe"));
        if (rails > 0) legend.Add(new(RailHex, "Rail"));

        if (c.Producing > 0) legend.Add(new(ProducingHex, "Producing", Square: true));
        if (c.Idle > 0) legend.Add(new(IdleHex, "Idle", Square: true));
        if (c.Paused > 0) legend.Add(new(PausedHex, "Paused", Square: true));
        if (c.Unconfigured > 0) legend.Add(new(UnsetHex, "No recipe", Square: true));

        if (generators > 0) legend.Add(new(GeneratorHex, "Generator"));
        if (extractors > 0) legend.Add(new(ExtractorHex, "Extractor"));
        if (layers.HasFlag(FactoryMapLayers.Nodes) && freeNodes > 0) legend.Add(new(NodeFreeHex, "Free node"));

        return legend;
    }

    /// <summary>
    /// The counts line under the scale bar. Leads with what is WRONG, because
    /// that is the reason somebody ran the command, and a map that buries three
    /// idle machines behind a total is just decoration.
    /// </summary>
    private static string Summary(
        BuildingCounts c, int belts, int pipes, int rails, int generators, int extractors, int freeNodes,
        FactoryMapLayers layers)
    {
        var parts = new List<string>();

        var stopped = c.Idle + c.Paused + c.Unconfigured;
        if (stopped > 0) parts.Add($"{stopped} machine(s) not producing");

        if (c.Total > 0) parts.Add($"{c.Total} machines");
        if (generators > 0) parts.Add($"{generators} generators");
        if (extractors > 0) parts.Add($"{extractors} extractors");
        if (belts > 0) parts.Add($"{belts} belts");
        if (pipes > 0) parts.Add($"{pipes} pipes");
        if (rails > 0) parts.Add($"{rails} rail segments");
        if (layers.HasFlag(FactoryMapLayers.Nodes)) parts.Add($"{freeNodes} free nodes");

        return string.Join("  ·  ", parts);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
