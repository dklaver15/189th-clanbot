using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Scales a clip up to 1440p (by its shorter side) before it goes to YouTube.
///
/// ── Why upscale at all ──
/// YouTube picks its encoder by upload resolution: 1440p and above get VP9 or
/// AV1 at a much higher bitrate, and that better encode is also what viewers
/// get when they watch at 1080p. A 1080p gameplay clip uploaded as-is gets the
/// older, starved H.264 encode and smears in motion; the same clip upscaled to
/// 1440p first does not. The upscale adds no detail of its own; it only buys
/// the better YouTube encode.
///
/// ── Never blocks an upload ──
/// <see cref="TryUpscaleAsync"/> returns null whenever it skips or fails
/// (already 1440p+, too long, HDR, ffmpeg missing, encode error, timeout), and
/// the caller uploads the original file. A clip always reaches YouTube.
///
/// ── Keeping the droplet healthy ──
/// One encode at a time (a second upload waits its turn), capped decoder,
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
    private readonly BotConfig _config;
    private readonly ILogger<VideoUpscaler> _logger;
    private readonly SemaphoreSlim _encodeGate = new(1, 1);

    public VideoUpscaler(IOptions<BotConfig> config, ILogger<VideoUpscaler> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Writes an upscaled copy of <paramref name="inputPath"/> into
    /// <paramref name="workDir"/> and returns its path, or null to upload the
    /// original. <paramref name="onEncodeStarting"/> runs once, just before a
    /// real encode begins (after any wait for the encode slot).
    /// </summary>
    public async Task<string?> TryUpscaleAsync(
        string inputPath, string workDir, Func<Task>? onEncodeStarting = null, CancellationToken ct = default)
    {
        if (!_config.VideoUploadUpscaleEnabled) return null;

        var target = _config.VideoUploadUpscaleTargetSize;
        try
        {
            var probe = await ProbeAsync(inputPath, ct);
            if (probe is null) return null;

            var shortSide = Math.Min(probe.Width, probe.Height);
            if (shortSide >= target)
            {
                _logger.LogInformation("Upscale skipped: clip is already {W}x{H}", probe.Width, probe.Height);
                return null;
            }
            if (probe.DurationSeconds > _config.VideoUploadUpscaleMaxMinutes * 60)
            {
                _logger.LogInformation(
                    "Upscale skipped: clip is {Minutes:F1} min, over the {Max}-min cap",
                    probe.DurationSeconds / 60, _config.VideoUploadUpscaleMaxMinutes);
                return null;
            }
            if (probe.IsHdr)
            {
                // Tone-mapping HDR to SDR is its own can of worms; YouTube handles
                // HDR uploads well as-is, so leave those alone.
                _logger.LogInformation("Upscale skipped: clip is HDR ({Transfer})", probe.ColorTransfer);
                return null;
            }

            await _encodeGate.WaitAsync(ct);
            try
            {
                if (onEncodeStarting is not null) await onEncodeStarting();
                return await EncodeAsync(inputPath, workDir, probe, target, ct);
            }
            finally
            {
                _encodeGate.Release();
            }
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

    private async Task<string?> EncodeAsync(string inputPath, string workDir, ProbeResult probe, int target, CancellationToken ct)
    {
        var output = Path.Combine(workDir, "upscaled.mkv");

        // The scale runs after ffmpeg's automatic rotation, so iw/ih are the
        // clip as it's meant to be seen: the shorter side goes to the target and
        // the longer side follows the aspect ratio (-2 keeps it even for x264).
        var scale = $"scale=w='if(gte(iw,ih),-2,{target})':h='if(gte(iw,ih),{target},-2)':flags=lanczos";

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
            "-i", inputPath,
            "-map", "0:v:0", "-map", "0:a?",
            "-filter_threads", "1",
            "-vf", scale,
            "-c:v", "libx264", "-preset", "veryfast",
            "-crf", _config.VideoUploadUpscaleCrf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-threads", "2",
            "-x264-params", "rc-lookahead=10:sliced-threads=1",
            "-c:a", "copy",
            output,
        };

        var timeout = TimeSpan.FromMinutes(_config.VideoUploadUpscaleTimeoutMinutes);
        var sw = Stopwatch.StartNew();
        var (exitCode, stderr) = await RunAsync(_config.FfmpegPath, args, timeout, lowPriority: true, ct);
        sw.Stop();

        if (exitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            _logger.LogWarning(
                "ffmpeg upscale exited {Code} after {Elapsed}; uploading the original. Tail: {Tail}",
                exitCode, sw.Elapsed, Tail(stderr));
            return null;
        }

        _logger.LogInformation(
            "Upscaled {W}x{H} clip ({InMb:F1} MB) to {Target}p in {Elapsed:mm\\:ss} ({OutMb:F1} MB)",
            probe.Width, probe.Height, new FileInfo(inputPath).Length / 1048576.0,
            target, sw.Elapsed, new FileInfo(output).Length / 1048576.0);
        return output;
    }

    private sealed record ProbeResult(int Width, int Height, double DurationSeconds, string? ColorTransfer)
    {
        public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
    }

    private async Task<ProbeResult?> ProbeAsync(string inputPath, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height,color_transfer:format=duration",
            "-of", "json",
            inputPath,
        };

        var (exitCode, stdout, stderr) = await RunCaptureAsync(_config.FfprobePath, args, TimeSpan.FromMinutes(1), ct);
        if (exitCode != 0)
        {
            _logger.LogWarning("ffprobe exited {Code}; skipping upscale. {Tail}", exitCode, Tail(stderr));
            return null;
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;

        var s = streams[0];
        var width  = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        var transfer = s.TryGetProperty("color_transfer", out var t) ? t.GetString() : null;

        double duration = 0;
        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var d)
            && d.GetString() is { } ds)
        {
            double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }

        if (width <= 0 || height <= 0) return null;
        return new ProbeResult(width, height, duration, transfer);
    }

    private async Task<(int ExitCode, string Stderr)> RunAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout, bool lowPriority, CancellationToken ct)
    {
        var (code, _, stderr) = await RunCoreAsync(exe, args, timeout, lowPriority, captureStdout: false, ct);
        return (code, stderr);
    }

    private Task<(int ExitCode, string Stdout, string Stderr)> RunCaptureAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunCoreAsync(exe, args, timeout, lowPriority: false, captureStdout: true, ct);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCoreAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout, bool lowPriority, bool captureStdout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {exe}");

        if (lowPriority)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not lower {Exe} priority", exe); }
        }

        // Drain both pipes concurrently: ffmpeg writes progress to stderr
        // continuously and would block once the pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (ct.IsCancellationRequested) throw;
            _logger.LogWarning("{Exe} timed out after {Timeout} and was killed", exe, timeout);
            return (-1, string.Empty, "timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, captureStdout ? stdout : string.Empty, stderr);
    }

    private static string Tail(string s, int max = 600) =>
        s.Length <= max ? s.Trim() : "…" + s[^max..].Trim();
}
