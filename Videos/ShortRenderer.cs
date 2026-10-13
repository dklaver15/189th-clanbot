using System.Globalization;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>The Short's start time is at or past the end of the clip.</summary>
public sealed class ShortTimeOutOfRangeException(double start, double duration)
    : Exception($"Start ({ShortRequest.FormatTime(start)}) is past the end of the clip ({ShortRequest.FormatTime(duration)}).");

/// <summary>
/// Makes a 1080x1920 YouTube Short from a clip: trims it to the chosen start/end
/// (at most <see cref="ShortRequest.MaxSeconds"/>) and frames it.
///
/// ── Framing ──
/// FullViewZoom puts the whole frame on top and a zoomed centre crop below, the
/// two heights worked out from the source shape so any landscape clip fills
/// exactly 1920. CenterCrop takes a centred 9:16 slice. Vertical and square clips
/// keep their shape (fitted inside 1080x1920), whatever layout was chosen, and
/// AsIs on a landscape clip falls back to FullViewZoom, so the result is always
/// vertical and YouTube classifies it as a Short.
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
    public async Task<string> RenderAsync(string inputPath, string workDir, ShortSettings settings, CancellationToken ct = default)
    {
        var probe = await ProbeAsync(inputPath, ct);
        if (probe.Duration > 0 && settings.StartSeconds >= probe.Duration)
            throw new ShortTimeOutOfRangeException(settings.StartSeconds, probe.Duration);

        var end = settings.EndSeconds ?? settings.StartSeconds + ShortRequest.MaxSeconds;
        if (probe.Duration > 0) end = Math.Min(end, probe.Duration);
        var length = end - settings.StartSeconds;

        var frame  = BuildFilter(settings.Layout, probe.Width, probe.Height);
        var filter = probe.Fps > 60 ? frame.Filter + ",fps=60" : frame.Filter;
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

        _logger.LogInformation(
            "Rendered Short ({Layout}, {Start}–{End}) from a {W}x{H} clip",
            settings.Layout, ShortRequest.FormatTime(settings.StartSeconds), ShortRequest.FormatTime(end),
            probe.Width, probe.Height);
        return output;
    }

    private sealed record Probe(int Width, int Height, double Duration, double Fps);

    /// <summary>Displayed size (rotation applied), duration and frame rate of the first video stream.</summary>
    private async Task<Probe> ProbeAsync(string inputPath, CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height,r_frame_rate:stream_side_data=rotation:format=duration",
            "-of", "json", inputPath,
        };
        var (exitCode, stdout, stderr) = await FfmpegProcess.RunAsync(
            _config.FfprobePath, args, TimeSpan.FromMinutes(1), lowPriority: false, captureStdout: true, _logger, ct);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffprobe exited {exitCode}: {FfmpegProcess.Tail(stderr, 300)}");

        using var doc = JsonDocument.Parse(stdout);
        var stream = doc.RootElement.GetProperty("streams")[0];
        var width  = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();

        var rotation = 0;
        if (stream.TryGetProperty("side_data_list", out var sideData))
            foreach (var d in sideData.EnumerateArray())
                if (d.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var deg)) rotation = deg;
        if (Math.Abs(rotation) % 180 == 90) (width, height) = (height, width);

        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var d2) && d2.GetString() is { } ds)
            double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        var fps = stream.TryGetProperty("r_frame_rate", out var rate) ? ParseRate(rate.GetString()) : 0;
        return new Probe(width, height, duration, fps);
    }

    private static double ParseRate(string? rate)
    {
        var parts = (rate ?? "").Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? n / d : 0;
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Nearest even integer (x264 needs even frame sizes).</summary>
    internal static int Even(double v) => (int)Math.Round(v / 2, MidpointRounding.AwayFromZero) * 2;

    /// <summary>Largest even integer not above <paramref name="v"/> (crops must stay inside the source).</summary>
    internal static int EvenDown(double v) => (int)Math.Floor(v / 2) * 2;
}
