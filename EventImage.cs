using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ClanGuardBot.Services;

/// <summary>
/// Helpers for optional event banner images. Bytes are stored in the DB (so the
/// image survives a droplet loss and rides the encrypted SQLite backups): on
/// <see cref="ClanEvent"/> for one-off events, on <see cref="ClanEventSeries"/>
/// for recurring ones. Series occurrences carry only the lightweight file name
/// and resolve the bytes from their series — avoiding a multi-MB blob copied
/// across every (including every archived) occurrence row.
///
/// Images are attached to the #events message itself (<c>attachment://name</c>)
/// rather than referenced by a Discord CDN URL, because those URLs now expire —
/// which would break the image on the first sort/edit re-post.
/// </summary>
public static class EventImage
{
    /// <summary>Max accepted image size. Keeps the DB (and its backups) sane.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Longest-side pixel cap applied by <see cref="Downscale"/>. Discord renders
    /// the embed's main image up to the embed's max width, so a wide (landscape)
    /// banner stored too small gets pinned narrow and upscaled (blurry); storing
    /// it nearer Discord's embed width makes it fill the post crisply. The
    /// trade-off: a square/portrait banner renders correspondingly taller.
    /// Tune to taste — higher = larger in the post.
    /// </summary>
    public const int MaxImageDimension = 512;

    public static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    /// <summary>
    /// Shrinks an image so its longest side is at most <see cref="MaxImageDimension"/>,
    /// re-encoding in the same format (animated GIFs are resized frame-by-frame).
    /// Images already within the cap are returned unchanged (no upscaling or
    /// needless re-encode). Any failure returns the original bytes so a resize
    /// hiccup never blocks event creation.
    /// </summary>
    public static byte[] Downscale(byte[] bytes, string? fileName)
    {
        try
        {
            using var image = Image.Load(bytes);

            var longest = Math.Max(image.Width, image.Height);
            if (longest <= MaxImageDimension) return bytes;

            var scale = (double)MaxImageDimension / longest;
            var w = Math.Max(1, (int)Math.Round(image.Width * scale));
            var h = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(x => x.Resize(w, h));

            using var ms = new MemoryStream();
            switch (Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": image.SaveAsJpeg(ms); break;
                case ".webp": image.SaveAsWebp(ms); break;
                case ".gif":  image.SaveAsGif(ms);  break;
                default:      image.SaveAsPng(ms);  break;
            }

            var outBytes = ms.ToArray();
            return outBytes.Length > 0 ? outBytes : bytes;
        }
        catch
        {
            return bytes;
        }
    }

    public static bool IsAllowedExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return Array.IndexOf(AllowedExtensions, ext) >= 0;
    }

    /// <summary>
    /// Produces a safe, Discord-friendly attachment file name with an allowed
    /// image extension. The stored name must match the attached file's name
    /// exactly for the embed's <c>attachment://</c> reference to resolve.
    /// </summary>
    public static string Sanitize(string? fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "event-image.png" : fileName.Trim();

        // Last path segment only, and drop any query string.
        name = name.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var q = name.IndexOf('?');
        if (q >= 0) name = name[..q];

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        name = sb.ToString();
        if (name.Length == 0) name = "event-image.png";

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext  = Path.GetExtension(name).ToLowerInvariant();
        if (Array.IndexOf(AllowedExtensions, ext) < 0) ext = ".png";
        if (string.IsNullOrEmpty(stem)) stem = "event-image";
        if (stem.Length > 60) stem = stem[..60];

        return stem + ext;
    }

    /// <summary>
    /// Resolves the image bytes + file name to attach when (re)posting an event.
    /// One-off events carry their own bytes; series occurrences fall back to the
    /// series' bytes. Returns (null, null) when the event has no image.
    /// </summary>
    public static async Task<(byte[]? bytes, string? fileName)> ResolveAsync(BotDbContext db, ClanEvent ev)
    {
        if (ev.ImageBytes is { Length: > 0 })
            return (ev.ImageBytes, ev.ImageFileName);

        if (ev.SeriesId is int sid)
        {
            var series = await db.ClanEventSeries.FirstOrDefaultAsync(s => s.Id == sid);
            if (series?.ImageBytes is { Length: > 0 })
                return (series.ImageBytes, series.ImageFileName);
        }

        return (null, null);
    }
}