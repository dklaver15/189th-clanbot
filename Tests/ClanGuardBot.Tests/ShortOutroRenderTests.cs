using ClanGuardBot.Models;
using ClanGuardBot.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Tests;

/// <summary>
/// Real ffmpeg renders with a stand-in outro (skipped where ffmpeg isn't installed):
/// solid blue clips, and an outro that is transparent apart from red boxes.
/// </summary>
public class ShortOutroRenderTests(ShortOutroRenderTests.StandIns standIns) : IClassFixture<ShortOutroRenderTests.StandIns>, IDisposable
{
    /// <summary>The stand-in outro and sound, made once for the class on first use (each test is a new instance).</summary>
    public sealed class StandIns : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("cgb-outro-assets-").FullName;
        private readonly Lazy<string> _outro;
        private readonly Lazy<string> _sound;

        public StandIns()
        {
            _outro = new(() => Make("outro.webm", p => Ffmpeg.MakeAlphaOutro(p, 2.5)));
            _sound = new(() => Make("sfx.wav", p => Ffmpeg.MakeSilence(p, 1)));
        }

        public string Outro => _outro.Value;
        public string Sound => _sound.Value;

        public void Dispose() => Directory.Delete(_dir, recursive: true);

        private string Make(string name, Action<string> make)
        {
            var path = Path.Combine(_dir, name);
            make(path);
            return path;
        }
    }

    private static readonly (int R, int G, int B) Red  = (255, 0, 0);
    private static readonly (int R, int G, int B) Blue = (0, 0, 255);

    private readonly string _dir = Directory.CreateTempSubdirectory("cgb-outro-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ShortRenderer Renderer(bool enabled = true, string? outro = null, string? sound = null) =>
        new(Options.Create(new BotConfig
            {
                VideoShortOutroEnabled   = enabled,
                VideoShortOutroFile      = outro ?? standIns.Outro,
                VideoShortOutroSoundFile = sound ?? Path.Combine(_dir, "no-sound.wav"),
            }),
            new EncodeGate(), NullLogger<ShortRenderer>.Instance);

    private string BlueClip(int w, int h, double seconds, bool audio = true)
    {
        var path = Path.Combine(_dir, $"blue-{w}x{h}-{seconds}-{audio}.mp4");
        if (!File.Exists(path)) Ffmpeg.MakeClip(path, w, h, seconds, audio, color: "blue");
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
    public async Task A_clip_whose_audio_runs_past_its_video_still_ends_on_the_outro()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        // 6 s of video, 8 s of audio: the Short is the video's 6 s, so the outro starts at 3.5 s.
        var clip = Path.Combine(_dir, "long-audio.mp4");
        Ffmpeg.MakeClip(clip, 1280, 720, 6, color: "blue", audioSeconds: 8);

        var output = await Renderer().RenderAsync(clip, _dir, Short());

        Assert.InRange(Ffmpeg.Probe(output).Duration, 5.8, 6.3);
        AssertNear(Red, Ffmpeg.PixelAt(output, 4.0, 540, 960));
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
        var mixed = await Renderer(sound: standIns.Sound).RenderAsync(clip, Directory.CreateDirectory(Path.Combine(_dir, "b")).FullName, Short());

        Assert.InRange(Ffmpeg.MeanVolumeDb(mixed) - Ffmpeg.MeanVolumeDb(plain), -1.0, 1.0);
    }

    [SkippableFact]
    public async Task A_clip_without_audio_gets_the_sound_effect_alone()
    {
        Skip.IfNot(Ffmpeg.Available, "ffmpeg isn't on PATH");
        var output = await Renderer(sound: standIns.Sound).RenderAsync(BlueClip(1280, 720, 6, audio: false), _dir, Short());

        var p = Ffmpeg.Probe(output);
        Assert.True(p.HasAudio);
        Assert.InRange(p.Duration, 5.8, 6.3);
    }
}
