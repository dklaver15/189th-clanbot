using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Thin Reddit API client. Two operations: fetch the newest N posts in a
/// subreddit, and fetch a user's profile (account age + karma). Handles
/// OAuth token acquisition + caching, the User-Agent dance Reddit's API
/// rules require, and 401 "token expired" recovery.
///
/// ── Why not Reddit.NET ──
/// We need exactly two endpoints. A general-purpose Reddit client adds
/// a transitive dependency tree, its own retry semantics, and surprise
/// behavior we'd have to reason about. Two endpoints, raw HttpClient,
/// System.Text.Json — small enough to keep in our heads.
///
/// ── Auth model ──
/// Reddit "script" apps use OAuth password grant: HTTP Basic header of
/// (clientId:clientSecret), body of grant_type=password&amp;username=...&amp;password=...
/// against https://www.reddit.com/api/v1/access_token. Token has TTL of
/// ~24h. We cache it in memory with a small refresh-before-expiry
/// margin so the polling loop never races a mid-cycle expiry.
///
/// ── Rate limit ──
/// OAuth API allows 60 req/min per token. Five subreddits + author
/// lookups for matched posts = well under 10 req per 10-min cycle.
/// We don't add explicit pacing because we never get close to the cap;
/// if that ever changes, the right place to add it is here.
/// </summary>
public sealed class RedditApiClient
{
    private const string TokenEndpoint = "https://www.reddit.com/api/v1/access_token";
    private const string OAuthBase = "https://oauth.reddit.com";

    /// <summary>
    /// Refresh the access token this much before it expires, so a request
    /// that started at TTL-30s doesn't cross the boundary mid-flight.
    /// </summary>
    private static readonly TimeSpan TokenRefreshLead = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly RedditLeadsOptions _options;
    private readonly ILogger<RedditApiClient> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _cachedToken;
    private DateTime _tokenExpiresUtc = DateTime.MinValue;

    public RedditApiClient(
        HttpClient httpClient,
        IOptions<RedditLeadsOptions> options,
        ILogger<RedditApiClient> logger)
    {
        _httpClient = httpClient;
        _options    = options.Value;
        _logger     = logger;
    }

    /// <summary>
    /// Newest N posts in a subreddit, in Reddit's listing order (most recent first).
    /// </summary>
    public async Task<IReadOnlyList<RedditPost>> GetNewPostsAsync(
        string subreddit,
        int limit,
        CancellationToken ct)
    {
        var url = $"{OAuthBase}/r/{subreddit}/new?limit={Math.Clamp(limit, 1, 100)}";
        var listing = await SendAsync<ListingEnvelope<PostThing>>(HttpMethod.Get, url, ct);
        if (listing?.Data?.Children is null) return Array.Empty<RedditPost>();

        var posts = new List<RedditPost>(listing.Data.Children.Count);
        foreach (var child in listing.Data.Children)
        {
            var d = child.Data;
            if (d is null) continue;
            posts.Add(new RedditPost(
                Id:        d.Id ?? string.Empty,
                Subreddit: d.Subreddit ?? subreddit,
                Author:    d.Author ?? "[deleted]",
                Title:     d.Title ?? string.Empty,
                Selftext:  d.Selftext ?? string.Empty,
                Permalink: d.Permalink ?? string.Empty,
                CreatedUtc: DateTimeOffset.FromUnixTimeSeconds((long)d.CreatedUtc).UtcDateTime,
                Stickied:  d.Stickied,
                Over18:    d.Over18,
                IsSelf:    d.IsSelf));
        }
        return posts;
    }

    /// <summary>
    /// Profile lookup for /user/X/about. Returns null if Reddit can't find
    /// the user (deleted account, suspended, etc.) — caller decides whether
    /// to skip or accept the lead with synthetic age/karma.
    /// </summary>
    public async Task<RedditUser?> GetUserAboutAsync(string username, CancellationToken ct)
    {
        var url = $"{OAuthBase}/user/{Uri.EscapeDataString(username)}/about";
        AboutEnvelope? envelope;
        try
        {
            envelope = await SendAsync<AboutEnvelope>(HttpMethod.Get, url, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var d = envelope?.Data;
        if (d is null) return null;

        var createdUtc = DateTimeOffset.FromUnixTimeSeconds((long)d.CreatedUtc).UtcDateTime;
        var ageDays = (int)(DateTime.UtcNow - createdUtc).TotalDays;
        var totalKarma = d.TotalKarma ?? (d.LinkKarma + d.CommentKarma);
        return new RedditUser(username, ageDays, totalKarma);
    }

    // ── HTTP plumbing ────────────────────────────────────────────────

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);

        using var request = BuildRequest(method, url, token);
        using var response = await _httpClient.SendAsync(request, ct);

        // If the cached token was nuked server-side (rotated, revoked, or
        // we miscalculated TTL), Reddit returns 401. Refresh once and retry.
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _logger.LogInformation("Reddit returned 401; refreshing token and retrying once");
            InvalidateToken();
            token = await GetAccessTokenAsync(ct);
            using var retryRequest = BuildRequest(method, url, token);
            using var retryResponse = await _httpClient.SendAsync(retryRequest, ct);
            retryResponse.EnsureSuccessStatusCode();
            return await retryResponse.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken) &&
            DateTime.UtcNow + TokenRefreshLead < _tokenExpiresUtc)
        {
            return _cachedToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-checked: another caller may have refreshed while we waited.
            if (!string.IsNullOrEmpty(_cachedToken) &&
                DateTime.UtcNow + TokenRefreshLead < _tokenExpiresUtc)
            {
                return _cachedToken;
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);

            var basic = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type", "password"),
                new KeyValuePair<string,string>("username",   _options.Username),
                new KeyValuePair<string,string>("password",   _options.Password),
            });

            using var response = await _httpClient.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Reddit token endpoint returned {Status}: {Body}",
                    response.StatusCode, body);
                throw new HttpRequestException(
                    $"Reddit token request failed ({(int)response.StatusCode}): {body}");
            }

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
                ?? throw new InvalidOperationException("Empty token response");

            _cachedToken = token.AccessToken;
            _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn);

            _logger.LogInformation(
                "Reddit access token acquired; expires in {Seconds}s", token.ExpiresIn);

            return _cachedToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private void InvalidateToken()
    {
        _cachedToken = null;
        _tokenExpiresUtc = DateTime.MinValue;
    }

    // ── DTOs ─────────────────────────────────────────────────────────

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private sealed class ListingEnvelope<T>
    {
        public ListingData<T>? Data { get; set; }
    }

    private sealed class ListingData<T>
    {
        public List<Thing<T>> Children { get; set; } = new();
    }

    private sealed class Thing<T>
    {
        public T? Data { get; set; }
    }

    // Aliased for readability at the call site (Listing<PostThing>) without exposing internal types.
    private sealed class PostThing
    {
        public string? Id { get; set; }
        public string? Subreddit { get; set; }
        public string? Author { get; set; }
        public string? Title { get; set; }
        public string? Selftext { get; set; }
        public string? Permalink { get; set; }

        [JsonPropertyName("created_utc")]
        public double CreatedUtc { get; set; }

        public bool Stickied { get; set; }

        [JsonPropertyName("over_18")]
        public bool Over18 { get; set; }

        [JsonPropertyName("is_self")]
        public bool IsSelf { get; set; }
    }

    private sealed class AboutEnvelope
    {
        public AboutData? Data { get; set; }
    }

    private sealed class AboutData
    {
        [JsonPropertyName("created_utc")]
        public double CreatedUtc { get; set; }

        [JsonPropertyName("link_karma")]
        public int LinkKarma { get; set; }

        [JsonPropertyName("comment_karma")]
        public int CommentKarma { get; set; }

        [JsonPropertyName("total_karma")]
        public int? TotalKarma { get; set; }
    }
}

/// <summary>
/// Minimal projection of a Reddit post — only the fields the matcher and
/// embed builder need. Avoids leaking the full envelope shape into the
/// rest of the codebase.
/// </summary>
public sealed record RedditPost(
    string Id,
    string Subreddit,
    string Author,
    string Title,
    string Selftext,
    string Permalink,
    DateTime CreatedUtc,
    bool Stickied,
    bool Over18,
    bool IsSelf);

public sealed record RedditUser(string Username, int AccountAgeDays, int TotalKarma);
