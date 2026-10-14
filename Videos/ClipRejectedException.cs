namespace ClanGuardBot.Services;

/// <summary>
/// The clip can't be processed as asked (HDR, Start past the end, an empty result,
/// not a video). The message is what the uploader is told; the Upload button goes
/// live again.
/// </summary>
public sealed class ClipRejectedException(string message) : Exception(message)
{
    public static ClipRejectedException StartPastEnd(double start, double duration) =>
        new($"Start ({ClipTime.Format(start)}) is past the end of the clip ({ClipTime.Format(duration)}).");

    public static ClipRejectedException CameOutEmpty() =>
        new("The clip came out empty. Is Start past the end of the clip?");
}
