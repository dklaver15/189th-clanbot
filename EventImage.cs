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

    /// <summary>
    /// Aspect ratio a banner is cropped to by <see cref="Widen"/>.
    ///
    /// ── Why this exists ──
    /// Discord scales an embed's main image to fit a 400x300 box on desktop and
    /// then sizes the WHOLE embed to the result. A square banner is therefore
    /// height-limited to 300px, renders only ~300px wide, and drags every field
    /// in the embed (Time, Host, the RSVP rosters) down to that width, so the
    /// post looks comically narrow next to a landscape one. Anything 4:3 OR WIDER
    /// hits the 400px cap instead, which is the widest an embed carrying an image
    /// can get. 4:3 is therefore the ratio that buys the full width for the least
    /// crop: a square banner loses ~12.5% off the top and ~12.5% off the bottom.
    ///
    /// Raise it (e.g. 16.0/9.0) for a shorter, more banner-shaped post at the
    /// cost of cutting a lot more off the art. Lower it to 1.0 to disable the
    /// crop entirely. Desktop width does NOT improve past 4:3.
    /// </summary>
    public const double MinAspectRatio = 4.0 / 3.0;

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

    /// <summary>
    /// Centre-crops a banner narrower than <see cref="MinAspectRatio"/> down to
    /// that ratio, so Discord renders it (and therefore the whole embed) at its
    /// full 400px width instead of pinning the post to the image's own narrow
    /// footprint. See <see cref="MinAspectRatio"/> for why.
    ///
    /// Cropping rather than letterboxing was chosen deliberately (2026-08-04).
    /// Padding would have needed transparent bars, which needs an alpha channel,
    /// which turns every JPEG into a PNG and therefore renames the file. It also
    /// buys nothing on mobile, where the embed is already screen-width and the
    /// bars would only shrink the art by about a quarter. Cropping keeps the
    /// banner full-bleed on phones, keeps the format and file name untouched, and
    /// gets the same 400px on desktop. The cost is the trimmed top and bottom, so
    /// art with a title or a face near an edge will lose some of it, and a tall
    /// portrait source loses a lot. If that ever becomes a problem, the fallback
    /// is to pad instead of crop past some height ratio.
    ///
    /// Idempotent: an image already at or past the ratio comes back byte-identical
    /// (same array reference) with its name untouched, so this is safe to run on
    /// every re-post. Any failure returns the original bytes, because a crop
    /// hiccup must never cost the post its image. Animated GIFs are cropped
    /// frame-by-frame and left alone if re-encoding would push them past
    /// <see cref="MaxBytes"/>.
    /// </summary>
    public static (byte[] bytes, string fileName) Widen(byte[] bytes, string? fileName)
    {
        var name = Sanitize(fileName);

        try
        {
            using var image = Image.Load(bytes);

            if (image.Width <= 0 || image.Height <= 0) return (bytes, name);
            if (image.Width >= image.Height * MinAspectRatio) return (bytes, name);

            // Keep the full width and take the height down to match, so the crop
            // only ever trims top and bottom.
            var targetW = image.Width;
            var targetH = Math.Max(1, (int)Math.Round(image.Width / MinAspectRatio));

            // Stay inside the stored-size cap. ResizeMode.Crop scales the source to
            // fill the box first, so this shrinks the whole banner rather than
            // cutting more off it.
            if (targetW > MaxImageDimension)
            {
                var scale = (double)MaxImageDimension / targetW;
                targetW = MaxImageDimension;
                targetH = Math.Max(1, (int)Math.Round(targetH * scale));
            }

            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size     = new Size(targetW, targetH),
                Mode     = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
            }));

            // Same format in, same format out, so the file name never changes.
            using var ms = new MemoryStream();
            switch (Path.GetExtension(name))
            {
                case ".jpg":
                case ".jpeg": image.SaveAsJpeg(ms); break;
                case ".webp": image.SaveAsWebp(ms); break;
                case ".gif":  image.SaveAsGif(ms);  break;
                default:      image.SaveAsPng(ms);  break;
            }

            var outBytes = ms.ToArray();
            if (outBytes.Length == 0 || outBytes.Length > MaxBytes) return (bytes, name);

            return (outBytes, name);
        }
        catch
        {
            return (bytes, name);
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
    ///
    /// Also runs <see cref="Widen"/> and writes the cropped result back onto
    /// whichever row owns the blob. Two reasons that happens HERE rather than only
    /// at upload. Every event already stored, including live recurring series,
    /// gets widened on its next re-post with no migration and no re-upload. And
    /// the write-back means the crop is paid for once instead of on every sort
    /// pass (the droplet is 1 vCPU and a sort re-posts every event in a loop).
    ///
    /// The write-back is deliberately NOT saved here. All three call sites
    /// (EventPublisher, EventChannelSorter, ClanEventReconciliationService) post
    /// the message and then SaveChangesAsync immediately, so it lands alongside
    /// the new MessageId. <see cref="Widen"/> keeps the file name identical, so
    /// EventRsvpInteractionHandler (which re-renders the embed from the STORED
    /// <see cref="ClanEvent.ImageFileName"/> without re-attaching) stays correct
    /// either way. Do not change that: if a future variant ever renames the file,
    /// the stored name and the live message's attachment could drift apart, and
    /// the banner would silently vanish on the next RSVP click.
    /// </summary>
    public static async Task<(byte[]? bytes, string? fileName)> ResolveAsync(BotDbContext db, ClanEvent ev)
    {
        if (ev.ImageBytes is { Length: > 0 })
        {
            var (bytes, name) = Widen(ev.ImageBytes, ev.ImageFileName);
            if (!ReferenceEquals(bytes, ev.ImageBytes)) ev.ImageBytes = bytes;
            if (name != ev.ImageFileName) ev.ImageFileName = name;
            return (bytes, name);
        }

        if (ev.SeriesId is int sid)
        {
            var series = await db.ClanEventSeries.FirstOrDefaultAsync(s => s.Id == sid);
            if (series?.ImageBytes is { Length: > 0 })
            {
                var (bytes, name) = Widen(series.ImageBytes, series.ImageFileName);
                if (!ReferenceEquals(bytes, series.ImageBytes)) series.ImageBytes = bytes;
                if (name != series.ImageFileName) series.ImageFileName = name;

                // The occurrence stores only the name (the blob lives on the
                // series), and that name is what the RSVP handler re-renders from.
                if (name != ev.ImageFileName) ev.ImageFileName = name;
                return (bytes, name);
            }
        }

        return (null, null);
    }
}
