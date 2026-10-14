using System.Diagnostics;
using System.Globalization;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Prepares the file for a normal YouTube upload: scales a clip up to 1440p (by
/// its shorter side) and, when the uploader set Start/End, cuts it.
///
/// ── Why upscale at all ──
/// YouTube picks its encoder by upload resolution: 1440p and above get VP9 or
/// AV1 at a much higher bitrate, and that better encode is also what viewers
/// get when they watch at 1080p. A 1080p gameplay clip uploaded as-is gets the
/// older, starved H.264 encode and smears in motion; the same clip upscaled to
/// 1440p first does not. The upscale adds no detail of its own; it only buys
/// the better YouTube encode.
///
/// ── Untrimmed: never blocks an upload ──
/// Without Start/End, every skip or failure (already 1440p+, too long, HDR,
/// ffmpeg missing, encode error, timeout) uploads the original file instead.
///
/// ── Trimmed: one exact cut ──
/// With Start/End the clip is re-encoded once, cut exactly (and upscaled when
/// it's smaller than 1440p, scaled down to 1440p when it's larger so the encode
/// never runs above the size the memory figure below was measured at). This
/// never falls back: refusals (HDR, Start past the end, an empty result) and
/// failures are reported, so an untrimmed clip is never uploaded by mistake.
/// A Start of 0 with an End at or past the clip's length is not a trim: it takes
/// the untrimmed path.
///
/// ── Keeping the droplet healthy ──
/// One encode at a time across upscales and Shorts (<see cref="EncodeGate"/>;
/// a second one waits its turn), capped decoder,
/// filter and encoder threads so peak memory stays near 320 MB inside the
/// 640m container (see EncodeAsync), and below-normal CPU priority so the
/// gateway and every other handler stay responsive while it runs.
///
/// Audio is copied untouched and the output is Matroska, which carries any
/// source audio codec (Opus from webm, AAC from mp4) without a re-encode.
/// YouTube accepts .mkv uploads.
/// </summary>
public sealed class VideoUpscaler
{
    private const string CutMessage = "Cutting the clip to your Start and End…";

    private readonly BotConfig _config;
    private readonly EncodeGate _gate;
    private readonly ILogger<VideoUpscaler> _logger;

    public VideoUpscaler(IOptions<BotConfig> config, EncodeGate gate, ILogger<VideoUpscaler> logger)
    {
        _config = config.Value;
        _gate   = gate;
        _logger = logger;
    }

    private string UpscaleMessage =>
        $"Making a {_config.VideoUploadUpscaleTargetSize}p version first so YouTube gives it a better encode. This can take a few minutes.";

    /// <summary>
    /// The file to upload for a normal YouTube video: <paramref name="inputPath"/>
    /// itself, or an encoded copy in <paramref name="workDir"/>. Untrimmed (start 0,
    /// no end) behaves as before; trimmed throws on refusals and failures.
    /// <paramref name="onEncodeStarting"/> receives the message to show the uploader
    /// once a real encode begins (after any wait for the encode slot).
    /// </summary>
    public async Task<string> PrepareAsync(
        string inputPath, string workDir, double startSeconds, double? endSeconds,
        Func<string, Task>? onEncodeStarting = null, CancellationToken ct = default)
    {
        var trimmed = startSeconds > 0 || endSeconds is not null;
        return trimmed
            ? await CutAsync(inputPath, workDir, startSeconds, endSeconds, onEncodeStarting, ct)
            : await TryUpscaleAsync(inputPath, workDir, onEncodeStarting, ct) ?? inputPath;
    }

    /// <summary>An upscaled copy, or null to upload the original.</summary>
    private async Task<string?> TryUpscaleAsync(
        string inputPath, string workDir, Func<string, Task>? onEncodeStarting, CancellationToken ct)
    {
        if (!_config.VideoUploadUpscaleEnabled) return null;
        try
        {
            var info = await MediaProbe.ProbeAsync(_config.FfprobePath, inputPath, _logger, ct);
            if (!WantsUpscale(info, info.Duration)) return null;

            string? output;
            using (await _gate.AcquireAsync(ct))
            {
                if (onEncodeStarting is not null) await onEncodeStarting(UpscaleMessage);
                output = await EncodeAsync(inputPath, workDir, info, start: null, length: null, scale: true, ct);
            }
            if (output is not null
                && await MediaProbe.DurationAsync(_config.FfprobePath, output, _logger, ct) < MediaProbe.MinUsefulSeconds)
            {
                _logger.LogWarning("Upscale came out empty; uploading the original file instead");
                return null;
            }
            return output;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upscale failed; uploading the original file instead");
            return null;
        }
    }

    /// <summary>One exact-cut encode (scaled to the target when the clip is smaller or larger).</summary>
    private async Task<string> CutAsync(
        string inputPath, string workDir, double start, double? end, Func<string, Task>? onEncodeStarting, CancellationToken ct)
    {
        var info = await MediaProbe.ProbeAsync(_config.FfprobePath, inputPath, _logger, ct);
        // "Whole clip" typed out (0 to the end or past it) is not a trim.
        if (start <= 0 && info.Duration > 0 && (end is null || end >= info.Duration))
            return await TryUpscaleAsync(inputPath, workDir, onEncodeStarting, ct) ?? inputPath;
        if (info.IsHdr)
            throw new ClipRejectedException("This clip is HDR, so it can't be trimmed. Leave Start and End empty to upload it whole.");
        if (info.Duration > 0 && start >= info.Duration)
            throw ClipRejectedException.StartPastEnd(start, info.Duration);

        // Unknown length and no End: encode to the end of the file.
        double? length = end is { } e
            ? (info.Duration > 0 ? Math.Min(e, info.Duration) : e) - start
            : info.Duration > 0 ? info.Duration - start : null;
        var upscale = _config.VideoUploadUpscaleEnabled && WantsUpscale(info, length ?? info.Duration);
        // Larger clips are scaled down so the encode never exceeds the target
        // size (memory); a clip exactly at the target is only cut.
        var scale = upscale || Math.Min(info.Width, info.Height) > _config.VideoUploadUpscaleTargetSize;

        string? output;
        using (await _gate.AcquireAsync(ct))
        {
            if (onEncodeStarting is not null) await onEncodeStarting(upscale ? UpscaleMessage : CutMessage);
            output = await EncodeAsync(inputPath, workDir, info, start, length, scale, ct);
        }

        if (output is null)
            throw new InvalidOperationException("Cutting the clip failed or took too long. Try again, or pick a shorter Start–End range.");
        // A Start past the end of a file whose length ffprobe can't read encodes
        // "successfully" into nothing; never hand that to YouTube.
        if (await MediaProbe.DurationAsync(_config.FfprobePath, output, _logger, ct) < MediaProbe.MinUsefulSeconds)
            throw ClipRejectedException.CameOutEmpty();
        return output;
    }

    /// <summary>Upscale only clips below the target, not too long, not HDR.</summary>
    private bool WantsUpscale(MediaInfo info, double lengthSeconds)
    {
        var target = _config.VideoUploadUpscaleTargetSize;
        if (Math.Min(info.Width, info.Height) >= target)
        {
            _logger.LogInformation("Upscale skipped: clip is already {W}x{H}", info.Width, info.Height);
            return false;
        }
        if (lengthSeconds > _config.VideoUploadUpscaleMaxMinutes * 60)
        {
            _logger.LogInformation(
                "Upscale skipped: clip is {Minutes:F1} min, over the {Max}-min cap",
                lengthSeconds / 60, _config.VideoUploadUpscaleMaxMinutes);
            return false;
        }
        if (info.IsHdr)
        {
            // Tone-mapping HDR to SDR is its own can of worms; YouTube handles
            // HDR uploads well as-is, so leave those alone.
            _logger.LogInformation("Upscale skipped: clip is HDR");
            return false;
        }
        return true;
    }

    /// <summary>One x264 encode: optional cut (start/length), optional scale to the target size. Null on failure.</summary>
    private async Task<string?> EncodeAsync(
        string inputPath, string workDir, MediaInfo info, double? start, double? length, bool scale, CancellationToken ct)
    {
        var target = _config.VideoUploadUpscaleTargetSize;
        var output = Path.Combine(workDir, scale ? "scaled.mkv" : "cut.mkv");

        // The scale runs after ffmpeg's automatic rotation, so iw/ih are the
        // clip as it's meant to be seen: the shorter side goes to the target and
        // the longer side follows the aspect ratio (-2 keeps it even for x264).
        var scaleFilter = $"scale=w='if(gte(iw,ih),-2,{target})':h='if(gte(iw,ih),{target},-2)':flags=lanczos";

        var args = new List<string>
        {
            // Errors only, no progress lines: stderr stays small enough to keep
            // whole and quote on failure.
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "error", "-y",
            // Memory caps. Left to itself ffmpeg gives the decoder, the scaler
            // and x264 a thread (and a frame buffer) per core; measured at
            // ~480 MB peak for a 1440p encode, which with the bot's own ~220 MB
            // breaks the 640m container. One decoder thread, one filter thread,
            // two sliced x264 threads and a 10-frame lookahead measured ~320 MB.
            // Frame threading buys nothing on the droplet's single vCPU anyway.
            "-threads", "1",
        };
        args.AddRange(["-i", inputPath]);
        // -ss after -i, not before: with the audio copied, an input-side seek
        // left the video's timestamps starting at Start (ffmpeg 9), so the file
        // reported Start + the cut as its length. This decodes up to Start,
        // which costs time on a long clip but cuts exactly.
        if (start is { } s) args.AddRange(["-ss", Seconds(s)]);
        if (length is { } l) args.AddRange(["-t", Seconds(l)]);
        args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-filter_threads", "1"]);
        if (scale) args.AddRange(["-vf", scaleFilter]);
        args.AddRange(
        [
            "-c:v", "libx264", "-preset", "veryfast",
            "-crf", _config.VideoUploadUpscaleCrf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-threads", "2",
            "-x264-params", "rc-lookahead=10:sliced-threads=1",
            "-c:a", "copy",
            output,
        ]);

        var timeout = TimeSpan.FromMinutes(_config.VideoUploadUpscaleTimeoutMinutes);
        var sw = Stopwatch.StartNew();
        var (exitCode, _, stderr) = await FfmpegProcess.RunAsync(
            _config.FfmpegPath, args, timeout, lowPriority: true, captureStdout: false, _logger, ct);
        sw.Stop();

        if (exitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            _logger.LogWarning(
                "ffmpeg {What} exited {Code} after {Elapsed}. Tail: {Tail}",
                scale ? "scale" : "cut", exitCode, sw.Elapsed, FfmpegProcess.Tail(stderr));
            return null;
        }

        if (scale)
            _logger.LogInformation(
                "Scaled {W}x{H} clip ({InMb:F1} MB) to {Target}p in {Elapsed:mm\\:ss} ({OutMb:F1} MB)",
                info.Width, info.Height, new FileInfo(inputPath).Length / 1048576.0,
                target, sw.Elapsed, new FileInfo(output).Length / 1048576.0);
        if (start is not null)
            _logger.LogInformation(
                "Cut {Start}–{End} from a {W}x{H} clip in {Elapsed:mm\\:ss} ({OutMb:F1} MB)",
                ClipTime.Format(start.Value), length is { } len ? ClipTime.Format(start.Value + len) : "end",
                info.Width, info.Height, sw.Elapsed, new FileInfo(output).Length / 1048576.0);
        return output;
    }

    private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);
}
