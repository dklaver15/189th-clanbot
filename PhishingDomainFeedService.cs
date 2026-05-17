using System.Text.Json;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.Services;

/// <summary>
/// Periodically fetches and caches the public phishing-domain feed
/// from <see href="https://phish.sinking.yachts/v2/all"/> (Sinking
/// Yachts), a community-maintained list of domains known to be used
/// in Discord-targeted scams (fake Nitro gifts, Steam phishing, token
/// grabbers, etc.). Used by
/// <see cref="Handlers.TokenGrabberScannerHandler"/> as one of three
/// signals (officer blocklist, this feed, IP-URL static pattern).
///
/// ── Why this is a separate service ──
/// The feed is shared state — every message scan needs O(1) lookup
/// against the current list. Refreshing it inline on every scan would
/// hammer the API; keeping a stale per-handler copy would mean every
/// handler instance loses its cache on restart. A hosted service that
/// owns the cache and exposes it via DI gives every consumer the same
/// snapshot and only pays the network cost once every 6 hours.
///
/// ── Cache shape ──
/// The snapshot is a <see cref="HashSet{T}"/> of lowercased domains
/// using <see cref="StringComparer.OrdinalIgnoreCase"/>. Memory cost
/// is small even at ~10k entries (rough estimate: 300KB). The
/// reference is replaced atomically on refresh — readers see either
/// the old set or the new set, never a torn one. No locking needed.
///
/// ── User-Agent ──
/// Sinking Yachts' docs ask consumers to identify themselves in the
/// User-Agent header. We send <c>ClanGuardBot/1.0</c>. If the maintainer
/// ever wants to throttle individual consumers, that lets them.
///
/// ── Failure handling ──
/// A failed refresh logs and shorter-retries (15 min) without
/// clobbering the existing snapshot. The handler keeps using the
/// most recent successful pull until a new one lands. If the bot has
/// NEVER successfully fetched the feed, <see cref="Contains"/> just
/// returns false — the scanner falls back to the officer blocklist
/// and static patterns. Fail-open posture; better than holding up
/// message processing on a third-party outage.
///
/// ── Mode-gating ──
/// If <see cref="BotConfig.TokenGrabberScannerMode"/> is <c>Off</c>,
/// the refresh loop runs but does no work — saves the API call cost
/// when the feature is disabled. The handler is independently
/// mode-gated so toggling between Off and AlertOnly takes effect on
/// the next message without restarting the bot.
/// </summary>
public sealed class PhishingDomainFeedService : BackgroundService
{
    private const string FeedUrl  = "https://phish.sinking.yachts/v2/all";
    private const string UserAgent = "ClanGuardBot/1.0 (Discord moderation bot for the 189th clan)";

    private static readonly TimeSpan StartupDelay     = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshInterval  = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiscordSocketClient _client;
    private readonly ILogger<PhishingDomainFeedService> _logger;
    private readonly BotConfig _config;

    // Snapshot is replaced atomically on refresh. Reads are lock-free.
    // Volatile to make sure readers across cores see the new reference
    // promptly rather than caching the old one.
    private volatile HashSet<string> _snapshot =
        new(StringComparer.OrdinalIgnoreCase);

    private DateTime? _lastRefreshUtc;

    public PhishingDomainFeedService(
        IHttpClientFactory httpClientFactory,
        DiscordSocketClient client,
        ILogger<PhishingDomainFeedService> logger,
        IOptions<BotConfig> config)
    {
        _httpClientFactory = httpClientFactory;
        _client            = client;
        _logger            = logger;
        _config            = config.Value;
    }

    /// <summary>Number of domains currently cached. 0 before first successful fetch.</summary>
    public int Count => _snapshot.Count;

    /// <summary>UTC timestamp of the last successful refresh, or null if none has succeeded yet.</summary>
    public DateTime? LastRefreshUtc => _lastRefreshUtc;

    /// <summary>
    /// O(1) lookup. Returns false if the feed hasn't been fetched yet
    /// or if the domain isn't in the current snapshot. Caller is
    /// responsible for any subdomain peeling — this is exact-match only.
    /// </summary>
    public bool Contains(string domain)
        => !string.IsNullOrEmpty(domain) && _snapshot.Contains(domain);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the Discord client to come up so logs are interleaved
        // with the rest of the startup sequence. We don't depend on the
        // Discord client for the fetch itself — this is just for tidy
        // log ordering and a sane "service is ready" signal.
        while (_client.ConnectionState != ConnectionState.Connected)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            if (stoppingToken.IsCancellationRequested) return;
        }

        await Task.Delay(StartupDelay, stoppingToken);

        _logger.LogInformation(
            "PhishingDomainFeedService starting. Refresh interval: {Interval}",
            RefreshInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var success = await RefreshAsync(stoppingToken);

            try
            {
                await Task.Delay(
                    success ? RefreshInterval : RetryAfterFailure,
                    stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        // Mode-gate: don't pay the API cost if the feature is Off. The
        // handler is also gated separately, so flipping to AlertOnly
        // takes effect immediately on the next message — but the next
        // scheduled refresh will populate the cache, which means there's
        // an up-to-6-hour window after enabling the feature where the
        // scanner has no feed data. Officer blocklist and static patterns
        // still work during that window; that's acceptable for a feature
        // expected to stay enabled long-term.
        var mode = (_config.TokenGrabberScannerMode ?? "Off").Trim();
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("PhishingDomainFeedService: feature is Off, skipping refresh");
            return true;
        }

        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            using var req = new HttpRequestMessage(HttpMethod.Get, FeedUrl);
            req.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var domains = JsonSerializer.Deserialize<string[]>(json);

            if (domains is null || domains.Length == 0)
            {
                _logger.LogWarning(
                    "PhishingDomainFeedService: Sinking Yachts returned empty payload — keeping existing snapshot");
                return false;
            }

            // Build new snapshot, normalize to lowercase. Atomic
            // reference swap; readers see either fully old or fully new.
            var newSnapshot = new HashSet<string>(
                domains.Where(d => !string.IsNullOrWhiteSpace(d))
                       .Select(d => d.Trim().ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            _snapshot = newSnapshot;
            _lastRefreshUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "PhishingDomainFeedService refreshed: {Count} phishing domains loaded",
                newSnapshot.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "PhishingDomainFeedService refresh failed — keeping previous snapshot ({Count} entries)",
                _snapshot.Count);
            return false;
        }
    }
}