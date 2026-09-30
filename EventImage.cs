using System.Text;
using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
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
    /// Aspect ratio a banner is widened to by <see cref="Widen"/> when it is
    /// narrower than this.
    ///
    /// ── Why this exists ──
    /// Discord scales an embed's main image to fit a 400x300 box on desktop and
    /// then sizes the WHOLE embed to the result. A square banner is therefore
    /// height-limited to 300px, renders only ~300px wide, and drags every field
    /// in the embed (Time, Host, the RSVP rosters) down to that width, so the
    /// post looks comically narrow next to a landscape one. Anything 4:3 or wider
    /// hits the 400px cap instead, which is the widest an embed carrying an image
    /// can get. 4:3 is therefore the ratio that buys the full width with the
    /// smallest change to the banner.
    ///
    /// Nothing is gained past 4:3, so a banner ALREADY wider than this keeps its
    /// own shape. Forcing everything to 4:3 would put pointless bars above and
    /// below a landscape poster that was already rendering perfectly.
    /// </summary>
    public const double MinAspectRatio = 4.0 / 3.0;

    /// <summary>
    /// Width, in pixels, that Discord renders an embed's main image at when the
    /// source is at least this wide.
    ///
    /// ── The second, less obvious cause of a narrow post ──
    /// Discord NEVER upscales an embed image. A banner whose source is only 340px
    /// wide renders at 340px no matter what its aspect ratio is, and the embed
    /// shrinks to match. Aspect ratio alone is therefore not enough: a small 4:3
    /// banner still produces a narrow post. <see cref="Downscale"/> deliberately
    /// never upscales either, so nothing else in the pipeline was fixing this.
    /// <see cref="Widen"/> is what puts a floor under it.
    /// </summary>
    private const int DiscordImageDisplayWidth = 400;

    /// <summary>
    /// Blur strength of the backdrop <see cref="Widen"/> paints behind a narrow
    /// banner, as a fraction of the canvas width. Big enough that no detail
    /// survives (the backdrop must never compete with the real banner), small
    /// enough to keep the colours of the art it came from.
    /// </summary>
    private const float BackdropBlurFraction = 1f / 28f;

    /// <summary>How far the backdrop is dimmed, so the sharp banner on top of it
    /// clearly reads as the foreground. 1.0 = untouched.</summary>
    private const float BackdropBrightness = 0.55f;

    public static readonly string[] AllowedExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    /// <summary>
    /// Decodes only the formats in <see cref="AllowedExtensions"/>. Decoding sniffs
    /// the bytes, not the name, so without this a TIFF renamed .png would still reach
    /// the TIFF decoder (and likewise BMP, ICO, EXR and the rest), which have had
    /// most of ImageSharp's security advisories.
    /// </summary>
    private static readonly DecoderOptions DecodeOptions = new()
    {
        Configuration = new Configuration(
            new PngConfigurationModule(),
            new JpegConfigurationModule(),
            new GifConfigurationModule(),
            new WebpConfigurationModule()),
    };

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
            using var image = Image.Load(DecodeOptions, bytes);

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
    /// Makes a banner render at Discord's full embed width, so the post stops
    /// being pinned to a narrow image. Two things can pin it, and this fixes both:
    /// a banner narrower than <see cref="MinAspectRatio"/> gets widened to that
    /// ratio, and a banner whose source is under
    /// <see cref="DiscordImageDisplayWidth"/> gets scaled up (Discord will not
    /// upscale it for us). A banner that is already wide enough on both counts is
    /// returned untouched.
    ///
    /// ── Nothing is ever cut off ──
    /// The banner is scaled to FIT the new canvas, never cropped, and whatever
    /// space is left over is filled with a blurred, dimmed, zoomed copy of the
    /// banner itself: the treatment a video player uses for a portrait clip. The
    /// frame ends up full of the art's own colours, so the post reads as
    /// deliberate rather than as an image floating in empty bars, and not one
    /// pixel of the original is lost.
    ///
    /// An earlier version centre-cropped to 4:3 instead. It was replaced on
    /// 2026-08-06 because it ate the title off the top and the detail bar off the
    /// bottom of real event posters, and (because it did nothing about width) it
    /// could leave a small banner both cut off AND still narrow. Do not
    /// reintroduce cropping here.
    ///
    /// Every treated banner comes out exactly <see cref="MaxImageDimension"/>
    /// wide, so every event post ends up the same width instead of each one being
    /// sized by whatever art someone happened to upload. Scaling a small banner up
    /// costs some sharpness, which is the accepted trade: Discord displays it at
    /// 400px regardless, so the alternative is not a crisper banner, it is a
    /// narrow post.
    ///
    /// The result covers the canvas completely, so there is no transparency to
    /// preserve and the image keeps its original format AND file name. Keep it
    /// that way: <see cref="ClanEvent.ImageFileName"/> is both the attachment name
    /// and what EventRsvpInteractionHandler re-renders <c>attachment://</c> from
    /// WITHOUT re-attaching, so a variant that renamed the file could drift out of
    /// sync with the live message and make the banner silently vanish on the next
    /// RSVP click.
    ///
    /// Animated GIFs get a cheaper treatment (a flat fill in a colour sampled from
    /// the banner's own edges, applied frame by frame) because compositing a
    /// blurred backdrop under every frame is not worth the CPU on a 1 vCPU box.
    ///
    /// Idempotent: its own output is already wide enough, so a second pass returns
    /// the same array reference and this is safe to run on every re-post. Any
    /// failure returns the original bytes, because a widening hiccup must never
    /// cost the post its banner.
    /// </summary>
    public static (byte[] bytes, string fileName) Widen(byte[] bytes, string? fileName)
    {
        var name = Sanitize(fileName);

        try
        {
            using var image = Image.Load<Rgba32>(DecodeOptions, bytes);

            if (image.Width <= 0 || image.Height <= 0) return (bytes, name);

            var wideEnough  = image.Width >= image.Height * MinAspectRatio;
            var largeEnough = image.Width >= DiscordImageDisplayWidth;
            if (wideEnough && largeEnough) return (bytes, name);

            // Keep a landscape banner's own shape; only a narrow one is widened.
            var ratio   = Math.Max(MinAspectRatio, (double)image.Width / image.Height);
            var targetW = MaxImageDimension;
            var targetH = Math.Max(1, (int)Math.Round(targetW / ratio));

            var isGif = Path.GetExtension(name) == ".gif";
            using var ms = new MemoryStream();

            if (isGif)
            {
                var pad = EdgeColor(image);
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size     = new Size(targetW, targetH),
                    Mode     = ResizeMode.Pad,
                    PadColor = pad,
                }));
                image.SaveAsGif(ms);
            }
            else
            {
                // Backdrop: the banner zoomed to cover the canvas, blurred past
                // recognition and dimmed.
                using var backdrop = image.Clone(x => x
                    .Resize(new ResizeOptions
                    {
                        Size     = new Size(targetW, targetH),
                        Mode     = ResizeMode.Crop,
                        Position = AnchorPositionMode.Center,
                    })
                    .GaussianBlur(Math.Max(1f, targetW * BackdropBlurFraction))
                    .Brightness(BackdropBrightness));

                // Foreground: the complete banner, scaled to fit, centred.
                using var front = image.Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(targetW, targetH),
                    Mode = ResizeMode.Max,
                }));

                using var canvas = new Image<Rgba32>(targetW, targetH);
                canvas.Mutate(x => x
                    .DrawImage(backdrop, new Point(0, 0), 1f)
                    .DrawImage(front, new Point((targetW - front.Width) / 2, (targetH - front.Height) / 2), 1f));

                switch (Path.GetExtension(name))
                {
                    case ".jpg":
                    case ".jpeg": canvas.SaveAsJpeg(ms); break;
                    case ".webp": canvas.SaveAsWebp(ms); break;
                    default:      canvas.SaveAsPng(ms);  break;
                }
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

    /// <summary>
    /// Average colour of the banner's left and right edge columns. Used as the
    /// flat fill behind an animated GIF, where a blurred backdrop is too
    /// expensive: sampling the edges means the fill usually continues whatever
    /// the art already has at its sides.
    /// </summary>
    private static Color EdgeColor(Image<Rgba32> image)
    {
        long r = 0, g = 0, b = 0;
        var right = image.Width - 1;
        for (var y = 0; y < image.Height; y++)
        {
            var l  = image[0, y];
            var rp = image[right, y];
            r += l.R + rp.R;
            g += l.G + rp.G;
            b += l.B + rp.B;
        }

        var n = Math.Max(1, image.Height * 2);
        return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
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
    /// Also runs <see cref="Widen"/> and writes the widened result back onto
    /// whichever row owns the blob. Two reasons that happens HERE rather than only
    /// at upload. Every event already stored, including live recurring series,
    /// gets widened on its next re-post with no migration and no re-upload. And
    /// the write-back means the compositing is paid for once instead of on every
    /// sort pass (the droplet is 1 vCPU and a sort re-posts every event in a loop).
    ///
    /// The write-back is deliberately NOT saved here. All three call sites
    /// (EventPublisher, EventChannelSorter, ClanEventReconciliationService) post
    /// the message and then SaveChangesAsync immediately, so it lands alongside
    /// the new MessageId.
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
