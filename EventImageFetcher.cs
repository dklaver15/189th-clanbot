using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace ClanGuardBot.Services;

/// <summary>
/// Resolves a user-supplied link (a direct image URL, or a Tenor/Giphy "share"
/// page) into image bytes — a keyless stand-in for a GIF picker.
///
/// ── Why this is more than "download the URL" ──
/// A Tenor/Giphy share link is usually an HTML page, not the GIF. So we fetch
/// the URL; if it's already an image we use it, otherwise we read the page's
/// og:image meta tag (preferring a .gif) and fetch that.
///
/// ── SSRF guard ──
/// The bot fetches an arbitrary user-supplied URL, so every request is guarded:
/// http(s) only, no embedded credentials, the host is resolved and every
/// resulting IP must be public (private / loopback / link-local — including the
/// 169.254.169.254 cloud-metadata address — CGNAT, unique-local and IPv4-mapped
/// equivalents are rejected), redirects are followed manually with the same
/// check at each hop, the body is size-capped while streaming, and only real
/// image content-types are accepted. (Residual DNS-rebinding risk is accepted:
/// only trusted organizers reach this, and the only output is a stored image.)
/// </summary>
public static class EventImageFetcher
{
    private const int MaxRedirects = 5;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect      = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout         = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public sealed record Result(bool Ok, byte[]? Bytes, string? FileName, string? Error);

    private sealed class GuardException : Exception
    {
        public GuardException(string message) : base(message) { }
    }

    public static async Task<Result> FromUrlAsync(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri))
            return new Result(false, null, null, "That doesn't look like a valid link.");

        try
        {
            var (body, contentType, finalUri) = await GetGuardedAsync(uri);

            // Direct image link (e.g. media.tenor.com/...gif, "Copy GIF Address").
            if (IsAllowedImage(contentType))
                return new Result(true, body, NameFor(contentType, Path.GetFileName(finalUri.LocalPath)), null);

            // Share/landing page → pull the media URL out of its OpenGraph tags.
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var media = ExtractMediaUrl(Encoding.UTF8.GetString(body), finalUri);
                if (media is null)
                    return new Result(false, null, null, "Couldn't find an image on that page — try a direct image link, or upload the file.");

                var (body2, ct2, final2) = await GetGuardedAsync(media);
                if (IsAllowedImage(ct2))
                    return new Result(true, body2, NameFor(ct2, Path.GetFileName(final2.LocalPath)), null);

                return new Result(false, null, null, "That page's image isn't a supported type (PNG/JPG/GIF/WebP).");
            }

            return new Result(false, null, null, "That link isn't an image (PNG/JPG/GIF/WebP).");
        }
        catch (GuardException gex)
        {
            return new Result(false, null, null, gex.Message);
        }
        catch (Exception)
        {
            return new Result(false, null, null, "Couldn't fetch that link.");
        }
    }

    private static async Task<(byte[] body, string contentType, Uri finalUri)> GetGuardedAsync(Uri uri)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await EnsurePublicAsync(current);

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ClanGuardBot/1.0)");
            req.Headers.Accept.ParseAdd("image/*,text/html;q=0.9,*/*;q=0.5");

            var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            try
            {
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is not null)
                {
                    var loc  = resp.Headers.Location;
                    var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                        throw new GuardException("That link redirects somewhere unsupported.");
                    current = next;
                    continue;
                }

                resp.EnsureSuccessStatusCode();

                if (resp.Content.Headers.ContentLength is long len && len > EventImage.MaxBytes)
                    throw new GuardException($"That image is too large. Max is {EventImage.MaxBytes / (1024 * 1024)} MB.");

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                var body = await ReadCappedAsync(resp, EventImage.MaxBytes);
                return (body, contentType, current);
            }
            finally
            {
                resp.Dispose();
            }
        }

        throw new GuardException("That link redirects too many times.");
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, long cap)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + read > cap)
                throw new GuardException($"That image is too large. Max is {cap / (1024 * 1024)} MB.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static async Task EnsurePublicAsync(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new GuardException("Only http(s) links are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new GuardException("Links with embedded credentials aren't allowed.");

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.DnsSafeHost, out var literal))
            addresses = new[] { literal };
        else
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost);

        if (addresses.Length == 0)
            throw new GuardException("Couldn't resolve that link's host.");

        foreach (var ip in addresses)
            if (!IsPublic(ip))
                throw new GuardException("That link resolves to a non-public address.");
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0 || b[0] == 10 || b[0] == 127) return false;               // this-network, 10/8, loopback
            if (b[0] == 169 && b[1] == 254) return false;                            // link-local + cloud metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;               // 172.16/12
            if (b[0] == 192 && b[1] == 168) return false;                            // 192.168/16
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;              // 100.64/10 CGNAT
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) return false;                 // 192.0.0/24
            if (b[0] >= 224) return false;                                           // multicast / reserved / broadcast
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            if (ip.Equals(IPAddress.IPv6Loopback) || ip.Equals(IPAddress.IPv6Any)) return false;
            if (ip.IsIPv4MappedToIPv6) return IsPublic(ip.MapToIPv4());
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                                 // fc00::/7 unique-local
            return true;
        }

        return false; // unknown address family
    }

    private static bool IsAllowedImage(string contentType)
    {
        var ct = contentType.ToLowerInvariant();
        return ct is "image/gif" or "image/png" or "image/jpeg" or "image/jpg" or "image/webp";
    }

    private static string NameFor(string contentType, string? hint)
    {
        var ext = contentType.ToLowerInvariant() switch
        {
            "image/gif"  => ".gif",
            "image/png"  => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg"  => ".jpg",
            "image/webp" => ".webp",
            _            => ".png",
        };
        var stem = string.IsNullOrWhiteSpace(hint) ? "event-image" : Path.GetFileNameWithoutExtension(hint);
        if (string.IsNullOrWhiteSpace(stem)) stem = "event-image";
        return EventImage.Sanitize(stem + ext);
    }

    private static Uri? ExtractMediaUrl(string html, Uri baseUri)
    {
        var head = html.Length > 200_000 ? html[..200_000] : html; // OpenGraph tags live in <head>
        var candidates = new List<string>();

        foreach (Match m in Regex.Matches(head, "<meta\\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Value;
            var key = (AttrValue(tag, "property") ?? AttrValue(tag, "name"))?.ToLowerInvariant();
            if (key is null) continue;
            if (key is "og:image" or "og:image:url" or "og:image:secure_url" or "twitter:image" or "twitter:image:src")
            {
                var content = AttrValue(tag, "content");
                if (!string.IsNullOrWhiteSpace(content))
                    candidates.Add(WebUtility.HtmlDecode(content));
            }
        }

        if (candidates.Count == 0) return null;

        var pick = candidates.FirstOrDefault(PathEndsWithGif) ?? candidates[0];
        return Uri.TryCreate(baseUri, pick, out var abs) ? abs : null;
    }

    private static string? AttrValue(string tag, string attr)
    {
        var m = Regex.Match(tag, attr + "\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool PathEndsWithGif(string url)
    {
        var q = url.IndexOf('?');
        var path = q >= 0 ? url[..q] : url;
        return path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
    }
}
