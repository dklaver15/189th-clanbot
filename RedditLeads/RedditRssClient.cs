using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Pulls newest posts from a subreddit via Reddit's public RSS/Atom feed.
/// No authentication, no app registration, no OAuth dance — just an HTTP
/// GET against a publicly cached endpoint.
///
/// ── Why RSS instead of the OAuth API ──
/// Reddit closed off new Data API app creation for non-moderation use
/// cases in 2024. The /prefs/apps form silently rejects new script apps
/// from accounts without an active moderation use case. Public RSS feeds
/// were never closed off — they're served from the same CDN as
/// reddit.com itself and require no credentials to consume.
///
/// ── What we lose vs. OAuth ──
/// • Author account age + karma are NOT in the feed. The author quality
///   floor (LeadMatcher.RejectAuthor) becomes a no-op. Throwaway/spam
///   accounts will leak through more often; officers will hit Skip more.
///   The keyword filter still does ~90% of the work.
/// • Stickied / NSFW flags aren't exposed. In practice the keyword
///   filter catches stickied mod announcements ("recruiting" almost
///   always appears in welcome posts and gets rejected), and NSFW
///   LFG posts are vanishingly rare.
/// • Reddit's RSS is updated-time-ordered, not strictly newest-first
///   like the API's /new endpoint. With our 24-hour MaxPostAgeHours
///   filter the difference is invisible — every recent post is still
///   inside our window.
///
/// ── Rate limit ──
/// Reddit's public RSS endpoints serve from CDN with no documented
/// per-IP rate limit, but conservative observation suggests 60 req/min
/// is safe — same ballpark as the OAuth API. Our cadence (5 subs every
/// 10 min = 30/hour) is well under that.
///
/// ── User-Agent matters ──
/// Reddit's edge network blocks generic UAs (curl, python-requests,
/// empty). The descriptive UA configured in RedditLeadsOptions.UserAgent
/// is set on the typed HttpClient at registration time, so every
/// request out of this class carries it.
/// </summary>
public sealed class RedditRssClient
{
    private const string FeedUrlPattern = "https://www.reddit.com/r/{0}/new.rss?limit={1}";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// Reddit prefixes post IDs with "t3_" everywhere — both API and Atom feed.
    /// We strip it so the value we store matches what we'd get from the OAuth
    /// API, keeping RedditLead.RedditPostId stable across any future re-pivot.
    /// </summary>
    private const string PostIdPrefix = "t3_";

    /// <summary>
    /// Reddit Atom feed wraps the post body in HTML comment markers
    /// "&lt;!-- SC_OFF --&gt;" and "&lt;!-- SC_ON --&gt;" plus a wrapping div.
    /// Compiled once at class-load.
    /// </summary>
    private static readonly Regex CommentStripper = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagStripper     = new(@"<[^>]+>",     RegexOptions.Compiled);
    private static readonly Regex WhitespaceCollapser = new(@"\s+",     RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly ILogger<RedditRssClient> _logger;

    public RedditRssClient(
        HttpClient httpClient,
        IOptions<RedditLeadsOptions> options,
        ILogger<RedditRssClient> logger)
    {
        _httpClient = httpClient;
        _logger     = logger;
        // Options is injected for parity with the previous OAuth-based client
        // and to keep the DI registration shape symmetric. Not currently
        // consumed at runtime — UserAgent is wired on the typed HttpClient.
        _ = options;
    }

    /// <summary>
    /// Newest N posts from a subreddit. Limit is clamped to [1, 100] to
    /// match Reddit's listing cap.
    /// </summary>
    public async Task<IReadOnlyList<RedditPost>> GetNewPostsAsync(
        string subreddit,
        int limit,
        CancellationToken ct)
    {
        var url = string.Format(FeedUrlPattern, subreddit, Math.Clamp(limit, 1, 100));

        using var response = await _httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Reddit occasionally serves 503 from its CDN under load; treat
            // every non-success as transient and let the caller's cycle
            // try again on the next poll.
            _logger.LogWarning(
                "Reddit RSS for r/{Sub} returned {Status}; skipping this subreddit for the cycle",
                subreddit, response.StatusCode);
            return Array.Empty<RedditPost>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        XDocument doc;
        try
        {
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogWarning(ex, "Failed to parse RSS feed for r/{Sub}; skipping cycle", subreddit);
            return Array.Empty<RedditPost>();
        }

        var entries = doc.Root?.Elements(Atom + "entry");
        if (entries is null) return Array.Empty<RedditPost>();

        var posts = new List<RedditPost>();
        foreach (var entry in entries)
        {
            var post = ParseEntry(entry, subreddit);
            if (post is not null) posts.Add(post);
        }
        return posts;
    }

    private static RedditPost? ParseEntry(XElement entry, string requestedSub)
    {
        // <id>t3_abc123</id> — strip prefix.
        var rawId = (string?)entry.Element(Atom + "id");
        if (string.IsNullOrEmpty(rawId)) return null;
        var postId = rawId.StartsWith(PostIdPrefix, StringComparison.Ordinal)
            ? rawId[PostIdPrefix.Length..]
            : rawId;

        // <title> always present.
        var title = (string?)entry.Element(Atom + "title") ?? string.Empty;

        // <link href="..."/> — Atom uses an attribute, not element text.
        var url = entry.Element(Atom + "link")?.Attribute("href")?.Value ?? string.Empty;

        // <published>YYYY-MM-DDTHH:MM:SS+TZ</published> — original creation time.
        var publishedRaw = (string?)entry.Element(Atom + "published");
        DateTime postedUtc;
        if (!DateTime.TryParse(
                publishedRaw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out postedUtc))
        {
            // Malformed timestamp — extremely rare. Default to now so the
            // post still surfaces if everything else parses; freshness
            // filtering at the service layer treats it as "just observed".
            postedUtc = DateTime.UtcNow;
        }

        // <author><name>/u/SomeUser</name></author> — strip the /u/ prefix.
        var authorRaw = (string?)entry.Element(Atom + "author")?.Element(Atom + "name") ?? string.Empty;
        var author = authorRaw.StartsWith("/u/", StringComparison.Ordinal)
            ? authorRaw[3..]
            : authorRaw;

        // <content type="html">&lt;...&gt;</content>. XDocument auto-decodes
        // the XML entities, so .Value is the raw HTML markup. We strip tags
        // for keyword matching + storage.
        var contentHtml = (string?)entry.Element(Atom + "content") ?? string.Empty;
        var selftext = HtmlToPlainText(contentHtml);

        // <category term="programming"/> — falls back to the requested sub
        // if the feed doesn't echo it (it always does for /r/X/new.rss but
        // a defensive default doesn't hurt).
        var subFromFeed = entry.Element(Atom + "category")?.Attribute("term")?.Value;
        var subreddit = string.IsNullOrEmpty(subFromFeed) ? requestedSub : subFromFeed;

        return new RedditPost(
            Id:        postId,
            Subreddit: subreddit,
            Author:    author,
            Title:     title,
            Selftext:  selftext,
            Permalink: url, // already a full https URL from the Atom feed
            CreatedUtc: postedUtc,
            // RSS doesn't expose these — see class doc. Always false.
            Stickied:  false,
            Over18:    false,
            IsSelf:    true);
    }

    private static string HtmlToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Order matters: strip comments first (they may contain markup-
        // like text we don't want to preserve), then tags, then collapse
        // whitespace, then decode entities (so <p>foo &amp; bar</p>
        // becomes "foo & bar", not "foo &amp; bar").
        var stripped = CommentStripper.Replace(html, " ");
        stripped     = TagStripper.Replace(stripped, " ");
        stripped     = WebUtility.HtmlDecode(stripped);
        stripped     = WhitespaceCollapser.Replace(stripped, " ").Trim();
        return stripped;
    }
}

/// <summary>
/// Minimal projection of a Reddit post — same shape as the previous
/// OAuth-API version so downstream code (LeadMatcher, RedditLeadService)
/// doesn't change. Stickied / Over18 / IsSelf are stubbed for RSS
/// compatibility; see RedditRssClient class doc for why.
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
