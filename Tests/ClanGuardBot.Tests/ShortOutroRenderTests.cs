using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Tests;

/// <summary>
/// Real ffmpeg renders with a stand-in outro (skipped where ffmpeg isn't installed):
/// solid blue clips, and an outro that is transparent apart from red boxes.
/// </summary>
public class ShortOutroRenderTests : IDisposable
{
    private static readonly (int R, int G, int B) Red  = (255, 0, 0);
    private static readonly (int R, int G, int B) Blue = (0, 0, 255);

    private readonly string _dir = Directory.CreateTempSubdirectory("cgb-outro-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Outro()
    {
        var path = Path.Combine(_dir, "outro.webm");
        if (!File.Exists(path)) Ffmpeg.MakeAlphaOutro(path, 2.5);
        return path;
    }

    private string Sound()
    {
        var path = Path.Combine(_dir, "sfx.wav");
        if (!File.Exists(path)) Ffmpeg.MakeSilence(path, 1);
        return path;
    }

    private ShortRenderer Renderer(bool enabled = true, string? outro = null, string? sound = null) =>
        new(Options.Create(new BotConfig
            {
                VideoShortOutroEnabled   = enabled,
                VideoShortOutroFile      = outro ?? Outro(),
                VideoShortOutroSoundFile = sound ?? Path.Combine(_dir, "no-sound.wav"),
            }),
            new EncodeGate(), NullLogger<ShortRenderer>.Instance);

    private string BlueClip(int w, int h, double seconds, bool audio = true)
    {
        var path = Path.Combine(_dir, $"blue-{w}x{h}-{seconds}-{audio}.mp4");
        if (!File.Exists(path)) Ffmpeg.MakeSolidClip(path, w, h, seconds, "blue", audio);
        return path;
    }

    private static UploadSettings Short(ShortLayout layout = ShortLayout.FullViewZoom, double start = 0) =>
        new(VideoSubmissionKind.Short, layout, start, null, "t");

    private static void AssertNear((int R, int G, int B) expected, (int R, int G, int B) actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= 40 && Math.Abs(expected.G - actual.G) <= 40 && Math.Abs(expected.B - actual.B) <= 40,
            $"expected about {expected}, got {actual}");
    }

    [SkippableFact]
    public async Task The_outro_covers_the_end_of_the_short_and_not_before()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        // 8 s clip from 0:02: a 6 s Short, so the 2.5 s outro starts at 3.5 s of the Short.
        var output = await Renderer().RenderAsync(BlueClip(1280, 720, 8), _dir, Short(start: 2));

        Assert.InRange(Ffmpeg.Probe(output).Duration, 5.8, 6.3);
        AssertNear(Red,  Ffmpeg.PixelAt(output, 5.5, 540, 960));
        AssertNear(Blue, Ffmpeg.PixelAt(output, 2.0, 540, 960));
        AssertNear(Blue, Ffmpeg.PixelAt(output, 5.5, 60, 60));   // transparent outro shows the clip
    }

    [SkippableFact]
    public async Task A_square_short_keeps_the_bottom_of_the_pill_in_view()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await Renderer().RenderAsync(BlueClip(1080, 1080, 6), _dir, Short(ShortLayout.AsIs));

        var p = Ffmpeg.Probe(output);
        Assert.Equal((1080, 1080), (p.Width, p.Height));
        // The bottom box (y 1500–1580 in the outro) lands at 980–1060 once moved up by 520.
        AssertNear(Red, Ffmpeg.PixelAt(output, 5.5, 540, 1020));
    }

    [SkippableFact]
    public async Task A_missing_outro_file_still_renders()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await Renderer(outro: Path.Combine(_dir, "missing.webm")).RenderAsync(BlueClip(1280, 720, 6), _dir, Short());

        var p = Ffmpeg.Probe(output);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        AssertNear(Blue, Ffmpeg.PixelAt(output, 5.5, 540, 960));
    }

    [SkippableFact]
    public async Task A_disabled_outro_is_not_drawn()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await Renderer(enabled: false).RenderAsync(BlueClip(1280, 720, 6), _dir, Short());

        AssertNear(Blue, Ffmpeg.PixelAt(output, 5.5, 540, 960));
    }

    [SkippableFact]
    public async Task A_sound_effect_does_not_lower_the_clip_audio()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var clip = BlueClip(1280, 720, 6);
        var plain = await Renderer().RenderAsync(clip, Directory.CreateDirectory(Path.Combine(_dir, "a")).FullName, Short());
        var mixed = await Renderer(sound: Sound()).RenderAsync(clip, Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName, Short());

        Assert.InRange(Ffmpeg.MeanVolumeDb(mixed) - Ffmpeg.MeanVolumeDb(plain), -1.0, 1.0);
    }

    [SkippableFact]
    public async Task A_clip_without_audio_gets_the_sound_effect_alone()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await Renderer(sound: Sound()).RenderAsync(BlueClip(1280, 720, 6, audio: false), _dir, Short());

        var p = Ffmpeg.Probe(output);
        Assert.True(p.HasAudio);
        Assert.InRange(p.Duration, 5.8, 6.3);
    }

    [SkippableFact]
    public async Task The_real_outro_asset_renders()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        Skip.IfNot(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "189th_outro.webm")), "the outro asset isn't built yet");
        var renderer = new ShortRenderer(Options.Create(new BotConfig()), new EncodeGate(), NullLogger<ShortRenderer>.Instance);

        var output = await renderer.RenderAsync(BlueClip(1280, 720, 6), _dir, Short());

        var p = Ffmpeg.Probe(output);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
        Assert.InRange(p.Duration, 5.8, 6.3);
    }
}
