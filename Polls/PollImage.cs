using ClanGuardBot.Models;

namespace ClanGuardBot.Services;

/// <summary>
/// Resolves the banner image to attach to a poll post. The default is the waving
/// 189th flag GIF bundled at <c>Assets/189th_banner.gif</c> (see the .csproj
/// Content include); a creator can override it per poll with their own
/// image/GIF/Giphy link, captured into <see cref="Poll.ImageBytes"/> at creation
/// the same way event banners are (<see cref="EventImageFetcher"/> +
/// <see cref="EventImage"/>).
///
/// Like event banners, bytes are attached to the message itself
/// (<c>attachment://name</c>) rather than referenced by a Discord CDN URL —
/// those URLs now expire, which would break the image on the first edit re-post.
///
/// Note: only anonymous polls (our custom embed) carry a banner. A native Discord
/// poll can't have a file attached in the same message as the poll object, so the
/// banner doesn't apply there.
/// </summary>
public static class PollImage
{
    public const string DefaultBannerFileName = "189th_banner.gif";

    private static readonly string DefaultBannerPath =
        Path.Combine(AppContext.BaseDirectory, "Assets", DefaultBannerFileName);

    // The default banner is read once and reused — it's a static asset that
    // doesn't change at runtime. Null means the file was missing at startup
    // (a missed deploy), in which case the poll simply posts without a banner.
    private static byte[]? _defaultCache;
    private static bool _defaultLoaded;

    /// <summary>
    /// Returns (bytes, fileName) to attach, or null when there's nothing to
    /// attach (custom image absent AND the default asset is missing on disk).
    /// </summary>
    public static (byte[] Bytes, string FileName)? Resolve(Poll poll)
    {
        if (poll.ImageBytes is { Length: > 0 } custom && !string.IsNullOrWhiteSpace(poll.ImageFileName))
            return (custom, poll.ImageFileName!);

        var def = LoadDefault();
        return def is { Length: > 0 } ? (def, DefaultBannerFileName) : null;
    }

    /// <summary>The file name the embed should reference, even before bytes are resolved.</summary>
    public static string FileNameFor(Poll poll) =>
        !string.IsNullOrWhiteSpace(poll.ImageFileName) ? poll.ImageFileName! : DefaultBannerFileName;

    private static byte[]? LoadDefault()
    {
        if (_defaultLoaded) return _defaultCache;
        _defaultLoaded = true;
        try
        {
            if (File.Exists(DefaultBannerPath))
                _defaultCache = File.ReadAllBytes(DefaultBannerPath);
        }
        catch
        {
            _defaultCache = null;
        }
        return _defaultCache;
    }
}
