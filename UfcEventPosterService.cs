using System.Collections.Concurrent;
using System.Text.Json;
using ClanGuardBot.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Resolves an "official picture" for a UFC event embed. SportsDataIO's scores
/// feed doesn't ship event posters, so we look the event up on Wikipedia via
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

    private async Task<string?> LookupAsync(string title, CancellationToken ct)
    {
        try
        {
            var url = $"{ApiBase}?action=query&format=json&redirects=1" +
                      "&prop=pageimages&piprop=original|thumbnail&pithumbsize=600" +
                      $"&titles={Uri.EscapeDataString(title)}";

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Wikimedia asks API clients to identify themselves with contact info.
            req.Headers.UserAgent.ParseAdd("ClanGuardBot/1.0 (https://github.com/; Discord bot for the 189th clan)");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("query", out var query) ||
                !query.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var page in pages.EnumerateObject())
            {
                // pageid "-1" / "missing" means no article matched.
                if (page.Value.TryGetProperty("missing", out _)) continue;

                if (page.Value.TryGetProperty("original", out var original) &&
                    original.TryGetProperty("source", out var origSrc) &&
                    origSrc.ValueKind == JsonValueKind.String)
                    return origSrc.GetString();

                if (page.Value.TryGetProperty("thumbnail", out var thumb) &&
                    thumb.TryGetProperty("source", out var thumbSrc) &&
                    thumbSrc.ValueKind == JsonValueKind.String)
                    return thumbSrc.GetString();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wikipedia poster lookup failed for {Title}", title);
            return null;
        }
    }
}
