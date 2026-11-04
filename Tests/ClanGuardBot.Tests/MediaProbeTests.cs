using ClanGuardBot.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClanGuardBot.Tests;

/// <summary>Real ffprobe on generated files (skipped where ffmpeg isn't installed).</summary>
public class MediaProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cgb-probe-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name) => Path.Combine(_dir, name);

    private static Task<MediaInfo> Probe(string path) => MediaProbe.ProbeAsync("ffprobe", path, NullLogger.Instance);

    [SkippableFact]
    public async Task Reports_the_displayed_size_of_a_rotated_clip()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("rot.mp4");
        Ffmpeg.MakeClip(path, 1600, 720, 1, rotation: 90);

        var info = await Probe(path);
        Assert.Equal((720, 1600), (info.Width, info.Height));
    }

    [SkippableFact]
    public async Task Reports_length_and_frame_rate()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("plain.mp4");
        Ffmpeg.MakeClip(path, 1280, 720, 2);

        var info = await Probe(path);
        Assert.InRange(info.Duration, 1.9, 2.2);
        Assert.InRange(info.VideoDuration, 1.9, 2.2);
        Assert.Equal(30, info.Fps, precision: 1);
    }

    [SkippableFact]
    public async Task A_matroska_clip_reports_its_video_length_from_its_tag()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        // MKV and WebM keep the stream's length only in a DURATION tag ("00:00:03.000000000").
        var path = File("plain.mkv");
        Ffmpeg.MakeClip(path, 640, 360, 3);

        Assert.InRange((await Probe(path)).VideoDuration, 2.9, 3.1);
    }

    [SkippableFact]
    public async Task A_clip_without_a_stored_length_reports_zero()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("live.webm");
        Ffmpeg.MakeClipWithoutDuration(path, 2);

        var info = await Probe(path);
        Assert.Equal(0, info.Duration);
        Assert.Equal(0, info.VideoDuration);
    }

    [SkippableFact]
    public async Task A_clip_without_a_stored_length_is_measured_from_its_packets()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("live.webm");
        Ffmpeg.MakeClipWithoutDuration(path, 2);

        Assert.InRange(await MediaProbe.PacketDurationAsync("ffprobe", path, NullLogger.Instance), 1.9, 2.1);
    }

    [SkippableFact]
    public async Task A_late_first_packet_does_not_add_to_the_measured_length()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("late.webm");
        Ffmpeg.MakeClipWithoutDuration(path, 2, startOffset: 3);

        Assert.InRange(await MediaProbe.PacketDurationAsync("ffprobe", path, NullLogger.Instance), 1.9, 2.1);
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Hdr_tagged_clips_are_flagged(bool hdr)
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File($"hdr-{hdr}.mp4");
        Ffmpeg.MakeClip(path, 1280, 720, 1, hdr: hdr);

        Assert.Equal(hdr, (await Probe(path)).IsHdr);
    }

    [SkippableFact]
    public async Task A_file_without_video_is_refused()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("audio.m4a");
        Ffmpeg.MakeAudioOnly(path, 1);

        var ex = await Assert.ThrowsAsync<ClipRejectedException>(() => Probe(path));
        Assert.Equal("This file has no video track.", ex.Message);
    }

    [SkippableFact]
    public async Task A_file_that_is_not_media_is_refused()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var path = File("not-a-video.mp4");
        await System.IO.File.WriteAllTextAsync(path, "hello");

        var ex = await Assert.ThrowsAsync<ClipRejectedException>(() => Probe(path));
        Assert.Equal("This file couldn't be read as a video.", ex.Message);
    }
}
