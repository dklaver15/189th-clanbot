using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Tests;

/// <summary>Pins today's upscale behaviour before its ffmpeg runner moves to FfmpegProcess.</summary>
public class VideoUpscalerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cgb-upscale-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static VideoUpscaler NewUpscaler() =>
        new(Options.Create(new BotConfig()), new EncodeGate(), NullLogger<VideoUpscaler>.Instance);

    [SkippableFact]
    public async Task A_720p_clip_is_upscaled_to_1440p()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var input = Path.Combine(_dir, "in.mp4");
        Ffmpeg.MakeClip(input, 1280, 720, 1);

        var output = await NewUpscaler().TryUpscaleAsync(input, _dir);

        Assert.NotNull(output);
        var p = Ffmpeg.Probe(output!);
        Assert.Equal((2560, 1440), (p.Width, p.Height));
        Assert.True(p.HasAudio);
    }

    [SkippableFact]
    public async Task A_1440p_clip_is_left_alone()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var input = Path.Combine(_dir, "in.mp4");
        Ffmpeg.MakeClip(input, 2560, 1440, 1);

        Assert.Null(await NewUpscaler().TryUpscaleAsync(input, _dir));
    }
}
