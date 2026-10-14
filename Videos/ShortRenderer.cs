using System.Globalization;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Makes a 1080x1920 YouTube Short from a clip: trims it to the chosen start/end
/// (just under <see cref="UploadRequest.MaxShortSeconds"/>, see <see cref="MaxRenderSeconds"/>)
/// and frames it.
///
/// ── Framing ──
/// FullViewZoom puts the whole frame on top and a zoomed centre crop below, the
/// two heights worked out from the source shape so any landscape clip fills
/// exactly 1920. CenterCrop takes a centred 9:16 slice. Vertical and square clips
/// keep their shape (fitted inside 1080x1920), whatever layout was chosen, and
/// AsIs on a landscape clip falls back to FullViewZoom, so the result is always
/// vertical and YouTube classifies it as a Short.
/// HDR clips are refused: re-encoding them to 8-bit without tone mapping would wash them out.
///
/// ── Droplet ──
/// Same limits as <see cref="VideoUpscaler"/>: one encode at a time
/// (<see cref="EncodeGate"/>), capped threads, BelowNormal priority, a timeout.
/// </summary>
public sealed class ShortRenderer
{
    public const int OutputWidth = 1080;
    public const int OutputHeight = 1920;

    private readonly BotConfig _config;
    private readonly EncodeGate _gate;
    private readonly ILogger<ShortRenderer> _logger;

    public ShortRenderer(IOptions<BotConfig> config, EncodeGate gate, ILogger<ShortRenderer> logger)
    {
        _config = config.Value;
        _gate   = gate;
        _logger = logger;
    }

    /// <summary>An ffmpeg video filter and the frame size it produces.</summary>
    public sealed record Frame(string Filter, int Width, int Height);

    /// <summary>The filter for <paramref name="layout"/> on a source shown at width x height.</summary>
    public static Frame BuildFilter(ShortLayout layout, int width, int height)
    {
        if (height >= width)
        {
            // Vertical or square: keep the shape, fit inside 1080x1920.
            var (w, h) = (long)width * OutputHeight >= (long)height * OutputWidth
                ? (OutputWidth, Even(OutputWidth * (double)height / width))
                : (Even(OutputHeight * (double)width / height), OutputHeight);
            return new Frame($"scale={w}:{h},setsar=1", w, h);
        }

        if (layout == ShortLayout.CenterCrop)
        {
            var cropW = EvenDown(height * 9.0 / 16);
            return new Frame($"crop={cropW}:{EvenDown(height)},scale={OutputWidth}:{OutputHeight},setsar=1",
                             OutputWidth, OutputHeight);
        }

        // FullViewZoom (and AsIs on a landscape source).
        var topH      = Even(OutputWidth * (double)height / width);
        var bottomH   = OutputHeight - topH;
        var aspect    = (double)OutputWidth / bottomH;           // width / height of the zoomed part
        var zoomW     = Math.Min(width, height * aspect);
        var zoomCropW = EvenDown(zoomW);
        var zoomCropH = EvenDown(zoomW / aspect);
        return new Frame(
            $"split[full][zoom];[full]scale={OutputWidth}:{topH}[top];" +
            $"[zoom]crop={zoomCropW}:{zoomCropH},scale={OutputWidth}:{bottomH}[bottom];" +
            "[top][bottom]vstack,setsar=1",
            OutputWidth, OutputHeight);
    }

    /// <summary>Renders the Short into <paramref name="workDir"/> and returns its path.</summary>
    public async Task<string> RenderAsync(string inputPath, string workDir, UploadSettings settings, CancellationToken ct = default)
    {
        var info = await MediaProbe.ProbeAsync(_config.FfprobePath, inputPath, _logger, ct);
        if (info.IsHdr)
            throw new ClipRejectedException("This clip is HDR, so it can't be made into a Short.");
        if (info.Duration > 0 && settings.StartSeconds >= info.Duration)
            throw ClipRejectedException.StartPastEnd(settings.StartSeconds, info.Duration);

        var length = RenderSeconds(settings.StartSeconds, settings.EndSeconds, info.Duration);

        var frame = BuildFilter(settings.Layout, info.Width, info.Height);
        // Drop frames before both scale branches run, not after.
        var filter = info.Fps > 60 ? "fps=60," + frame.Filter : frame.Filter;
        var output = Path.Combine(workDir, "short.mp4");

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-y",
            "-threads", "1",
            "-ss", Seconds(settings.StartSeconds), "-i", inputPath, "-t", Seconds(length),
            "-map", "0:v:0", "-map", "0:a:0?",
            "-filter_threads", "1",
            "-vf", filter,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
            "-threads", "2", "-x264-params", "rc-lookahead=10:sliced-threads=1",
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            output,
        };

        using (await _gate.AcquireAsync(ct))
        {
            var (exitCode, _, stderr) = await FfmpegProcess.RunAsync(
                _config.FfmpegPath, args, TimeSpan.FromMinutes(_config.VideoShortRenderTimeoutMinutes),
                lowPriority: true, captureStdout: false, _logger, ct);
            if (exitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException($"ffmpeg exited {exitCode}: {FfmpegProcess.Tail(stderr, 300)}");
        }

        // ffprobe can't always tell a clip's length (some WebMs carry none), and a
        // Start past the end then encodes "successfully" into a file with nothing in
        // it. Never hand that to YouTube.
        if (await MediaProbe.DurationAsync(_config.FfprobePath, output, _logger, ct) < MediaProbe.MinUsefulSeconds)
            throw ClipRejectedException.CameOutEmpty();

        _logger.LogInformation(
            "Rendered Short ({Layout}, {Start}–{End}) from a {W}x{H} clip",
            settings.Layout, ClipTime.Format(settings.StartSeconds), ClipTime.Format(settings.StartSeconds + length),
            info.Width, info.Height);
        return output;
    }

    /// <summary>
    /// Longest Short rendered: just under <see cref="UploadRequest.MaxShortSeconds"/>.
    /// A render of exactly 3:00 can come out a few ms over once muxed, and YouTube
    /// then files it as a normal video instead of a Short.
    /// </summary>
    internal const double MaxRenderSeconds = UploadRequest.MaxShortSeconds - 0.5;

    /// <summary>
    /// Seconds of the clip to render from <paramref name="start"/>: up to
    /// <paramref name="end"/> (null = the end of the clip), never past the clip's end
    /// when its <paramref name="duration"/> is known (0 = unknown), and at most
    /// <see cref="MaxRenderSeconds"/>.
    /// </summary>
    internal static double RenderSeconds(double start, double? end, double duration)
    {
        var stop = end ?? double.MaxValue;
        if (duration > 0) stop = Math.Min(stop, duration);
        return Math.Min(stop - start, MaxRenderSeconds);
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Nearest even integer (x264 needs even frame sizes).</summary>
    internal static int Even(double v) => (int)Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2;

    /// <summary>Largest even integer not above <paramref name="v"/> (crops must stay inside the source).</summary>
    internal static int EvenDown(double v) => (int)Math.Floor(v / 2) * 2;
}
