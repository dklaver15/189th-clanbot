using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ClanGuardBot.Tests;

/// <summary>Real ffmpeg for render tests: generated test clips and ffprobe checks.</summary>
internal static class Ffmpeg
{
    public static readonly bool Available = Run("ffmpeg", "-version").ExitCode == 0;

    /// <summary>A test-pattern clip, optionally with a 440 Hz tone, a rotation tag (phone clips) and HDR (PQ) colour tags.</summary>
    public static void MakeClip(string path, int width, int height, double seconds, bool audio = true, int rotation = 0, bool hdr = false)
    {
        var s = seconds.ToString(CultureInfo.InvariantCulture);
        var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        args.AddRange(["-t", s, "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        // x264's own VUI params: the -color_trc output option is dropped by libx264 on current ffmpeg builds.
        if (hdr) args.AddRange(["-x264-params", "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        args.Add(path);
        var made = Run("ffmpeg", args.ToArray());
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
        if (rotation == 0) return;

        var tmp = path + ".rot.mp4";
        var rotated = Run("ffmpeg", "-v", "error", "-y", "-display_rotation", rotation.ToString(CultureInfo.InvariantCulture),
                          "-i", path, "-c", "copy", tmp);
        if (rotated.ExitCode != 0) throw new InvalidOperationException(rotated.Stderr);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// A WebM written in live mode: it carries no duration at all, so ffprobe
    /// can't say how long it is (some real clips are like this).
    /// </summary>
    public static void MakeClipWithoutDuration(string path, double seconds)
    {
        var made = Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
                       "-f", "lavfi", "-i", "sine", "-t", seconds.ToString(CultureInfo.InvariantCulture),
                       "-c:v", "libvpx", "-deadline", "realtime", "-c:a", "libopus", "-f", "webm", "-live", "1", path);
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
    }

    /// <summary>An audio-only file (no video track).</summary>
    public static void MakeAudioOnly(string path, double seconds)
    {
        var made = Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine", "-t",
                       seconds.ToString(CultureInfo.InvariantCulture), "-c:a", "aac", path);
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
    }

    public static (int Width, int Height, double Duration, bool HasAudio) Probe(string path)
    {
        var r = Run("ffprobe", "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "json", path);
        using var doc = JsonDocument.Parse(r.Stdout);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToList();
        var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
        var duration = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), duration,
                streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"));
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit(120_000);
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
