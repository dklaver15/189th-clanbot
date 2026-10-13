using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Tests;

/// <summary>Real ffmpeg renders of generated clips (skipped where ffmpeg isn't installed).</summary>
public class ShortRendererRenderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cgb-short-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ShortRenderer NewRenderer() =>
        new(Options.Create(new BotConfig()), new EncodeGate(), NullLogger<ShortRenderer>.Instance);

    private string Clip(int w, int h, double seconds, bool audio = true, int rotation = 0)
    {
        var path = Path.Combine(_dir, $"in-{w}x{h}-{rotation}.mp4");
        Ffmpeg.MakeClip(path, w, h, seconds, audio, rotation);
        return path;
    }

    [SkippableTheory]
    [InlineData(ShortLayout.FullViewZoom)]
    [InlineData(ShortLayout.CenterCrop)]
    public async Task A_landscape_clip_becomes_a_trimmed_1080x1920_short(ShortLayout layout)
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await NewRenderer().RenderAsync(Clip(1280, 720, 3), _dir, new ShortSettings(layout, 1, 3, "t"));

        var p = Ffmpeg.Probe(output);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.InRange(p.Duration, 1.8, 2.3);
        Assert.True(p.HasAudio);
    }

    [SkippableFact]
    public async Task A_clip_without_audio_still_renders()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await NewRenderer().RenderAsync(Clip(1280, 720, 2, audio: false), _dir,
            new ShortSettings(ShortLayout.FullViewZoom, 0, null, "t"));

        var p = Ffmpeg.Probe(output);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.False(p.HasAudio);
    }

    [SkippableFact]
    public async Task A_rotated_phone_clip_is_treated_as_vertical()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        // Stored 1280x720 with a 90° display rotation: 720x1280 on screen.
        var output = await NewRenderer().RenderAsync(Clip(1280, 720, 1, rotation: 90), _dir,
            new ShortSettings(ShortLayout.AsIs, 0, null, "t"));

        var p = Ffmpeg.Probe(output);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
    }

    [SkippableFact]
    public async Task A_start_past_the_end_is_refused()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var ex = await Assert.ThrowsAsync<ShortTimeOutOfRangeException>(() =>
            NewRenderer().RenderAsync(Clip(1280, 720, 2), _dir, new ShortSettings(ShortLayout.FullViewZoom, 5, null, "t")));
        Assert.Equal("Start (0:05) is past the end of the clip (0:02).", ex.Message);
    }
}
