namespace ClanGuardBot.Services;

/// <summary>
/// The clip can't be processed as asked (e.g. HDR, Start past the end, too short for
/// a Short, not a video). The message is what the uploader is told; the Upload
/// button goes live again.
/// </summary>
public sealed class ClipRejectedException(string message) : Exception(message)
{
    public static ClipRejectedException StartPastEnd(double start, double duration) =>
        new($"Start ({ClipTime.Format(start)}) is past the end of the clip ({ClipTime.Format(duration)}).");

    public static ClipRejectedException CameOutEmpty() =>
        new("The clip came out empty. Is Start past the end of the clip?");

    /// <summary>A Short shorter than <see cref="UploadRequest.MinShortSeconds"/>; floored so 4.7 s reads 0:04.</summary>
    public static ClipRejectedException TooShortForShort(double seconds) =>
        new($"A Short must be at least {ClipTime.Format(UploadRequest.MinShortSeconds)} long, and this one would only be {ClipTime.Format(Math.Floor(seconds))}.");

    public static ClipRejectedException UnknownLength() =>
        new("I couldn't tell how long this clip is, so it can't be made into a Short.");
}
