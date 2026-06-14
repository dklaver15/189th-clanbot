using System.Collections.Concurrent;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Resolves an "official picture" for a UFC event embed. The data feed doesn't
/// ship event posters, so we look the event up on Wikipedia via
/// the keyless MediaWiki API and use the page's lead image — for a UFC event
/// that's almost always the official promotional poster.
///
/// ── Why Wikipedia ──
/// It's free, keyless, and the canonical event pages ("UFC 300",
/// "UFC Fight Night: …") reliably carry the official poster as the lead image.
/// Best-effort by design: if the title doesn't resolve (odd naming, a brand-new
/// event with no page yet) we return null and the caller falls back to the
/// configured <see cref="BotConfig.UfcDefaultImageUrl"/> banner.
///
/// ── Caching ──
/// Posters don't change, so a resolved URL (or a "none" result) is cached for
/// 12 hours keyed by event name — the reminder loop and the slash commands
/// don't each hit Wikipedia for the same card.
/// </summary>
public sealed class UfcEventPosterService
{
    private const string ApiBase = "https://en.wikipedia.org/w/api.php";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UfcEventPosterService> _logger;
    private readonly BotConfig _config;

    // name → (url-or-null, expiry). A cached null means "looked, found nothing".
    private readonly ConcurrentDictionary<string, (string? Url, DateTime ExpiresUtc)> _cache = new();

    public UfcEventPosterService(
        IHttpClientFactory httpClientFactory,
        ILogger<UfcEventPosterService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _config = config.Value;
    }

    /// <summary>
    /// Resolves a poster image URL for the given event. Returns the configured
    /// fallback (possibly empty) when Wikipedia lookup is disabled or finds
    /// nothing. Never throws.
    /// </summary>
    public async Task<string?> GetPosterUrlAsync(string? eventName, CancellationToken ct = default)
    {
        var fallback = string.IsNullOrWhiteSpace(_config.UfcDefaultImageUrl) ? null : _config.UfcDefaultImageUrl;

        if (!_config.UfcUseWikipediaPoster || string.IsNullOrWhiteSpace(eventName))
            return fallback;

        var key = eventName.Trim();
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.ExpiresUtc)
            return cached.Url ?? fallback;

        var resolved = await LookupAsync(key, ct);
        _cache[key] = (resolved, DateTime.UtcNow.Add(CacheTtl));
        return resolved ?? fallback;
    }

    /// <summary>
    /// Resolves a poster by trying, in order: (1) the exact event name as a page
    /// title, (2) the part before the colon (so "UFC 311: A vs. B" → "UFC 311",
    /// which is the real article title), and (3) a fuzzy MediaWiki search,
    /// accepting only a top hit whose title starts with "UFC" so we never attach
    /// a wildly unrelated image. Returns null if nothing matches.
    /// </summary>
    private async Task<string?> LookupAsync(string eventName, CancellationToken ct)
    {
        // 1. Exact title (redirects followed — most real cards have a redirect
        //    from the full "UFC NNN: A vs. B" name to the article).
        var byTitle = await TryTitleAsync(eventName, ct);
        if (byTitle is not null) return byTitle;

        // 2. The portion before the colon — turns "UFC 311: Makhachev vs. Moicano"
        //    into "UFC 311", the actual article title for numbered events.
        var colon = eventName.IndexOf(':');
        if (colon > 0)
        {
            var head = eventName[..colon].Trim();
            if (head.Length > 0 && !head.Equals(eventName, StringComparison.OrdinalIgnoreCase))
            {
                var byHead = await TryTitleAsync(head, ct);
                if (byHead is not null) return byHead;
            }
        }

        // 3. Fuzzy search as a last resort, guarded to UFC-titled results.
        return await TrySearchAsync(eventName, ct);
    }

    private async Task<string?> TryTitleAsync(string title, CancellationToken ct)
    {
        var url = $"{ApiBase}?action=query&format=json&redirects=1" +
                  "&prop=pageimages&piprop=original|thumbnail&pithumbsize=600" +
                  $"&titles={Uri.EscapeDataString(title)}";

        var root = await FetchJsonAsync(url, ct);
        if (root is not { } el) return null;

        if (!el.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var page in pages.EnumerateObject())
        {
            if (page.Value.TryGetProperty("missing", out _)) continue;
            if (ExtractImage(page.Value) is { } img) return img;
        }

        return null;
    }

    private async Task<string?> TrySearchAsync(string queryText, CancellationToken ct)
    {
        var url = $"{ApiBase}?action=query&format=json&generator=search" +
                  $"&gsrsearch={Uri.EscapeDataString(queryText)}&gsrlimit=3&gsrnamespace=0" +
                  "&prop=pageimages&piprop=original|thumbnail&pithumbsize=600";

        var root = await FetchJsonAsync(url, ct);
        if (root is not { } el) return null;

        if (!el.TryGetProperty("query", out var query) ||
            !query.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Object)
            return null;

        // generator results are keyed by pageid (unordered); "index" carries the
        // search rank. Take the best-ranked page that's a UFC article with an image.
        var ranked = pages.EnumerateObject()
            .Select(p => p.Value)
            .OrderBy(v => v.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : int.MaxValue);

        foreach (var page in ranked)
        {
            var title = page.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title is null || !title.StartsWith("UFC", StringComparison.OrdinalIgnoreCase))
                continue;
            if (ExtractImage(page) is { } img) return img;
        }

        return null;
    }

    private static string? ExtractImage(JsonElement page)
    {
        if (page.TryGetProperty("original", out var original) &&
            original.TryGetProperty("source", out var origSrc) &&
            origSrc.ValueKind == JsonValueKind.String)
            return origSrc.GetString();

        if (page.TryGetProperty("thumbnail", out var thumb) &&
            thumb.TryGetProperty("source", out var thumbSrc) &&
            thumbSrc.ValueKind == JsonValueKind.String)
            return thumbSrc.GetString();

        return null;
    }

    private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Wikimedia asks API clients to identify themselves with contact info.
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (https://github.com/; Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            // Clone so the element stays valid after the JsonDocument is disposed.
            return doc.RootElement.Clone();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wikipedia poster lookup failed for {Url}", url);
            return null;
        }
    }
}
