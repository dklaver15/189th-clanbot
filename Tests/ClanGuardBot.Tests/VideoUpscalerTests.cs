using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Tests;

/// <summary>Real ffmpeg: what PrepareAsync hands to YouTube for a normal video.</summary>
public class VideoUpscalerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cgb-upscale-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static VideoUpscaler NewUpscaler() =>
        new(Options.Create(new BotConfig()), new EncodeGate(), NullLogger<VideoUpscaler>.Instance);

    private string Clip(int w, int h, double seconds, bool hdr = false)
    {
        var path = Path.Combine(_dir, $"in-{Guid.NewGuid():N}.mp4");
        Ffmpeg.MakeClip(path, w, h, seconds, hdr: hdr);
        return path;
    }

    [SkippableFact]
    public async Task An_untrimmed_720p_clip_is_upscaled_to_1440p()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var input = Clip(1280, 720, 1);

        var output = await NewUpscaler().PrepareAsync(input, _dir, 0, null);

        Assert.NotEqual(input, output);
        var p = Ffmpeg.Probe(output);
        Assert.Equal((2560, 1440), (p.Width, p.Height));
        Assert.True(p.HasAudio);
    }

    [SkippableFact]
    public async Task An_untrimmed_1440p_clip_is_uploaded_as_it_is()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var input = Clip(2560, 1440, 1);

        Assert.Equal(input, await NewUpscaler().PrepareAsync(input, _dir, 0, null));
    }

    [SkippableFact]
    public async Task An_untrimmed_hdr_clip_is_uploaded_as_it_is()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var input = Clip(1280, 720, 1, hdr: true);

        Assert.Equal(input, await NewUpscaler().PrepareAsync(input, _dir, 0, null));
    }

    [SkippableFact]
    public async Task A_trimmed_720p_clip_is_cut_and_upscaled()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await NewUpscaler().PrepareAsync(Clip(1280, 720, 4), _dir, 1, 3);

        var p = Ffmpeg.Probe(output);
        Assert.Equal((2560, 1440), (p.Width, p.Height));
        Assert.InRange(p.Duration, 1.8, 2.3);
    }

    [SkippableFact]
    public async Task A_trimmed_1440p_clip_is_cut_without_scaling()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await NewUpscaler().PrepareAsync(Clip(2560, 1440, 4), _dir, 1, 3);

        var p = Ffmpeg.Probe(output);
        Assert.Equal((2560, 1440), (p.Width, p.Height));
        Assert.InRange(p.Duration, 1.8, 2.3);
    }

    [SkippableFact]
    public async Task A_trimmed_hdr_clip_is_refused()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var ex = await Assert.ThrowsAsync<ClipRejectedException>(() =>
            NewUpscaler().PrepareAsync(Clip(1280, 720, 2, hdr: true), _dir, 1, null));
        Assert.Equal("This clip is HDR, so it can't be trimmed. Leave Start and End empty to upload it whole.", ex.Message);
    }

    [SkippableFact]
    public async Task A_start_past_the_end_is_refused()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var ex = await Assert.ThrowsAsync<ClipRejectedException>(() =>
            NewUpscaler().PrepareAsync(Clip(1280, 720, 2), _dir, 5, null));
        Assert.Equal("Start (0:05) is past the end of the clip (0:02).", ex.Message);
    }

    [SkippableFact]
    public async Task A_trimmed_clip_with_unknown_length_and_start_past_its_end_is_refused()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var input = Path.Combine(_dir, "live.webm");
        Ffmpeg.MakeClipWithoutDuration(input, 2);

        var ex = await Assert.ThrowsAsync<ClipRejectedException>(() => NewUpscaler().PrepareAsync(input, _dir, 5, null));
        Assert.Equal("The clip came out empty. Is Start past the end of the clip?", ex.Message);
    }
}
