using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>What ffprobe says about a clip's first video stream, as shown on screen.</summary>
/// <param name="Duration">Seconds; 0 when the file doesn't say.</param>
/// <param name="Fps">Average frame rate; 0 when unknown.</param>
public sealed record MediaInfo(int Width, int Height, double Duration, double Fps, bool IsHdr);

/// <summary>Reads clips with ffprobe for <see cref="VideoUpscaler"/> and <see cref="ShortRenderer"/>.</summary>
public static class MediaProbe
{
    /// <summary>An encoded file shorter than this has nothing in it.</summary>
    public const double MinUsefulSeconds = 0.1;

    /// <summary>
    /// Size (rotation applied: a phone clip stored 1920x1080 with a 90° tag is
    /// 1080x1920), length (format, else stream; 0 if neither says), frame rate
    /// (average, else nominal) and whether it's HDR (PQ or HLG). Files that aren't
    /// readable video throw <see cref="ClipRejectedException"/>.
    /// </summary>
    public static async Task<MediaInfo> ProbeAsync(string ffprobePath, string path, ILogger logger, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries",
            "stream=width,height,avg_frame_rate,r_frame_rate,color_transfer,duration:stream_side_data=rotation:format=duration",
            "-of", "json", path,
        };
        var (exitCode, stdout, stderr) = await FfmpegProcess.RunAsync(
            ffprobePath, args, TimeSpan.FromMinutes(1), lowPriority: false, captureStdout: true, logger, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("ffprobe exited {Code} on {Path}: {Tail}", exitCode, path, FfmpegProcess.Tail(stderr, 300));
            throw new ClipRejectedException("This file couldn't be read as a video.");
        }

        using var doc = ParseJson(stdout);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new ClipRejectedException("This file has no video track.");

        var stream = streams[0];
        var width  = stream.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 0;
        var height = stream.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : 0;
        if (width <= 0 || height <= 0)
            throw new ClipRejectedException("This file has no video track.");

        var rotation = 0;
        if (stream.TryGetProperty("side_data_list", out var sideData))
            foreach (var d in sideData.EnumerateArray())
                if (d.TryGetProperty("rotation", out var r) && r.TryGetInt32(out var deg)) rotation = deg;
        if (Math.Abs(rotation) % 180 == 90) (width, height) = (height, width);

        var duration = ParseSeconds(root.TryGetProperty("format", out var format) ? Text(format, "duration") : null);
        if (duration <= 0) duration = ParseSeconds(Text(stream, "duration"));

        var fps = ParseRate(Text(stream, "avg_frame_rate"));
        if (fps <= 0) fps = ParseRate(Text(stream, "r_frame_rate"));

        var transfer = Text(stream, "color_transfer");
        return new MediaInfo(width, height, duration, fps, transfer is "smpte2084" or "arib-std-b67");
    }

    /// <summary>A file's length in seconds from its container; 0 when ffprobe can't read one.</summary>
    public static async Task<double> DurationAsync(string ffprobePath, string path, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, stdout, _) = await FfmpegProcess.RunAsync(
            ffprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), lowPriority: false, captureStdout: true, logger, ct);
        return exitCode == 0 ? ParseSeconds(stdout.Trim()) : 0;
    }

    /// <summary>
    /// The first audio track's channel layout ("stereo", "mono", …): "" when the
    /// file doesn't say, null when there is no audio track.
    /// </summary>
    public static async Task<string?> AudioLayoutAsync(string ffprobePath, string path, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, stdout, _) = await FfmpegProcess.RunAsync(
            ffprobePath, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type,channel_layout", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), lowPriority: false, captureStdout: true, logger, ct);
        var parts = stdout.Trim().Split(',');
        if (exitCode != 0 || parts[0] != "audio") return null;
        return parts.Length > 1 ? parts[1].Trim() : "";
    }

    /// <summary>
    /// How long the first video track runs: its own stored length, else measured
    /// from its packets. Not the file's length, which also covers audio that runs
    /// on past the picture. 0 when neither can be read.
    /// </summary>
    public static async Task<double> VideoDurationAsync(string ffprobePath, string path, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, stdout, _) = await FfmpegProcess.RunAsync(
            ffprobePath, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), lowPriority: false, captureStdout: true, logger, ct);
        var stored = exitCode == 0 ? ParseSeconds(stdout.Trim()) : 0;
        return stored > 0 ? stored : await PacketDurationAsync(ffprobePath, path, logger, ct);
    }

    /// <summary>
    /// The first video track's length from its packets, first to last (a late first
    /// packet doesn't count), for files that don't store one (some WebMs). Reads the
    /// packets without decoding them; 0 when there are none.
    /// </summary>
    public static async Task<double> PacketDurationAsync(string ffprobePath, string path, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, stdout, _) = await FfmpegProcess.RunAsync(
            ffprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,duration_time", "-of", "csv=p=0", path],
            TimeSpan.FromMinutes(1), lowPriority: false, captureStdout: true, logger, ct);
        if (exitCode != 0) return 0;

        double? first = null;
        var end = 0.0;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)) continue;
            first = Math.Min(first ?? pts, pts);
            end = Math.Max(end, pts + (parts.Length > 1 ? ParseSeconds(parts[1]) : 0));
        }
        return first is { } start ? Math.Max(0, end - start) : 0;
    }

    private static JsonDocument ParseJson(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new ClipRejectedException("This file couldn't be read as a video.");
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double ParseSeconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 0;

    private static double ParseRate(string? rate)
    {
        var parts = (rate ?? "").Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0
            ? n / d : 0;
    }
}
