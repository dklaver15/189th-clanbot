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

    /// <summary>A clip of one flat colour (an ffmpeg colour name), optionally with a 440 Hz tone.</summary>
    public static void MakeSolidClip(string path, int width, int height, double seconds, string color, bool audio = true)
    {
        var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", $"color=c={color}:s={width}x{height}:r=30" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"]);
        args.AddRange(["-t", seconds.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac"]);
        args.Add(path);
        var made = Run("ffmpeg", args.ToArray());
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
    }

    /// <summary>
    /// A stand-in outro: 1080x1920 VP9 with alpha, transparent except two opaque
    /// red boxes, one dead centre (340,760 400x400) and one where the pill's bottom
    /// edge is (440,1500 200x80).
    /// </summary>
    public static void MakeAlphaOutro(string path, double seconds)
    {
        var s = seconds.ToString(CultureInfo.InvariantCulture);
        var made = Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i",
                       $"color=c=black@0.0:s=1080x1920:r=30:d={s},format=yuva420p," +
                       "drawbox=x=340:y=760:w=400:h=400:color=red@1:t=fill:replace=1," +
                       "drawbox=x=440:y=1500:w=200:h=80:color=red@1:t=fill:replace=1",
                       "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0",
                       "-deadline", "realtime", "-cpu-used", "8", path);
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
    }

    /// <summary>Silence: stereo 48 kHz WAV (a stand-in sound effect).</summary>
    public static void MakeSilence(string path, double seconds)
    {
        var made = Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
                       "-t", seconds.ToString(CultureInfo.InvariantCulture), path);
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
    }

    /// <summary>The colour of one pixel of the frame shown at <paramref name="seconds"/>.</summary>
    public static (int R, int G, int B) PixelAt(string path, double seconds, int x, int y)
    {
        var raw = path + $".{seconds}-{x}-{y}.rgb";
        var made = Run("ffmpeg", "-v", "error", "-y", "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", path,
                       "-frames:v", "1", "-vf", $"crop=1:1:{x}:{y},format=rgb24", "-f", "rawvideo", raw);
        if (made.ExitCode != 0) throw new InvalidOperationException(made.Stderr);
        var px = File.ReadAllBytes(raw);
        File.Delete(raw);
        return (px[0], px[1], px[2]);
    }

    /// <summary>The file's average audio level in dB (ffmpeg's volumedetect).</summary>
    public static double MeanVolumeDb(string path)
    {
        var r = Run("ffmpeg", "-hide_banner", "-nostats", "-i", path, "-vn", "-af", "volumedetect", "-f", "null", "-");
        var m = System.Text.RegularExpressions.Regex.Match(r.Stderr, @"mean_volume: (-?[0-9.]+) dB");
        if (!m.Success) throw new InvalidOperationException(r.Stderr);
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
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
