using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ClanGuardBot.Services;

/// <summary>Runs ffmpeg / ffprobe for the video features.</summary>
internal static class FfmpegProcess
{
    /// <summary>
    /// Runs <paramref name="exe"/> and returns its exit code and output. A run past
    /// <paramref name="timeout"/> is killed (whole tree) and reported as exit -1.
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout, bool lowPriority, bool captureStdout,
        ILogger logger, CancellationToken ct)
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
            catch (Exception ex) { logger.LogDebug(ex, "Could not lower {Exe} priority", exe); }
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
            logger.LogWarning("{Exe} timed out after {Timeout} and was killed", exe, timeout);
            return (-1, string.Empty, "timed out");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, captureStdout ? stdout : string.Empty, stderr);
    }

    /// <summary>The last <paramref name="max"/> characters, for quoting ffmpeg errors in logs.</summary>
    public static string Tail(string s, int max = 600) =>
        s.Length <= max ? s.Trim() : "…" + s[^max..].Trim();
}
