using ClanGuardBot.Data;
using ClanGuardBot.Models;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClanGuardBot.RedditLeads;

/// <summary>
/// Polls the configured subreddits on a fixed cadence via Reddit's public
/// RSS feed, runs each fresh post through LeadMatcher, and posts surviving
/// posts to the leads channel as RedditLead embeds.
///
/// ── Pipeline order ──
/// For each subreddit, for each post in /new.rss:
///   1. Recency floor (post created within MaxPostAgeHours) — kills cold-start floods.
///   2. Dedupe by RedditPostId — listing returns the same posts cycle to cycle.
///   3. LeadMatcher.MatchText — positive keyword + negative keyword.
///   4. Insert row, post embed, stamp DiscordMessageId.
///
/// Previous OAuth-based pipeline had a tier-3 author quality floor that
/// fetched /user/X/about for karma + account age. RSS doesn't expose those
/// fields, so that step is gone — the keyword filter is the whole filter
/// stack now. See RedditRssClient class doc for why we're on RSS at all.
///
/// ── Failure isolation ──
/// One bad subreddit must not kill the cycle. Each subreddit is wrapped
/// in its own try/catch; a 503 from Reddit's CDN on r/Battlefield doesn't
/// stop r/FindAClan from being polled. Same for individual posts within
/// a subreddit: one parse failure shouldn't blow up the rest of the listing.
///
/// ── Embed posting failures ──
/// If the Discord post throws after the DB row was already committed,
/// the row stays with DiscordMessageId = 0 and is never retried. This
/// is the deliberate trade: we'd rather lose the visible embed for a
/// single lead than re-flood the channel with a duplicate when the next
/// poll re-finds the post. The /leads recent slash command surfaces
/// orphaned (DiscordMessageId == 0) rows so officers can investigate
/// if they ever stack up.
///
/// ── Disabled mode ──
/// When RedditLeadsOptions.Enabled is false the service registers but
/// the loop returns immediately. Cheaper than conditional registration
/// and keeps the DI graph the same in dev/prod.
/// </summary>
public sealed class RedditLeadService : BackgroundService
{
    /// <summary>
    /// Newest-N pull size. Reddit's RSS endpoint accepts a limit param
    /// up to 100; 25 gives us plenty of headroom over a 10-min window
    /// even for the busiest sub we watch (r/Battlefield) without paginating.
    /// </summary>
    private const int ListingLimit = 25;

    /// <summary>
    /// Storage cap for the post body. Matches RedditLeadEmbedBuilder's
    /// ExcerptLimit so the embed never has to re-truncate a longer stored
    /// value. Stored as part of the row so a later edit/delete on Reddit
    /// can't rewrite our snapshot.
    /// </summary>
    private const int ExcerptStorageLimit = 350;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly RedditRssClient _reddit;
    private readonly RedditLeadsOptions _options;
    private readonly ILogger<RedditLeadService> _logger;

    public RedditLeadService(
        IServiceProvider services,
        DiscordSocketClient client,
        RedditRssClient reddit,
        IOptions<RedditLeadsOptions> options,
        ILogger<RedditLeadService> logger)
    {
        _services = services;
        _client   = client;
        _reddit   = reddit;
        _options  = options.Value;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("RedditLeadService is disabled (RedditLeads:Enabled=false). Loop will not run.");
            return;
        }

        // Same readiness pattern as AwolCheckService — wait for the gateway
        // to settle so the leads channel lookup doesn't race the Ready event.
        while (_client.ConnectionState != ConnectionState.Connected || !_client.Guilds.Any())
        {
            _logger.LogInformation("RedditLeadService waiting for Discord client to be ready...");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var subs = _options.GetSubredditsList();
        _logger.LogInformation(
            "RedditLeadService started (RSS mode). Polling {Count} subreddit(s) every {Min} min: {Subs}",
            subs.Count, _options.PollingIntervalMinutes, string.Join(", ", subs));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RedditLeadService poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_options.PollingIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var subs = _options.GetSubredditsList();
        var freshFloor = DateTime.UtcNow.AddHours(-_options.MaxPostAgeHours);

        var totalSurfaced = 0;

        foreach (var sub in subs)
        {
            try
            {
                totalSurfaced += await ProcessSubredditAsync(sub, freshFloor, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing subreddit r/{Sub}", sub);
            }
        }

        if (totalSurfaced > 0)
            _logger.LogInformation("RedditLead cycle: {Count} new lead(s) surfaced across all subs", totalSurfaced);
    }

    private async Task<int> ProcessSubredditAsync(string sub, DateTime freshFloor, CancellationToken ct)
    {
        var posts = await _reddit.GetNewPostsAsync(sub, ListingLimit, ct);
        if (posts.Count == 0) return 0;

        // Step 1: cheap recency filter before any DB work.
        var recent = posts.Where(p => p.CreatedUtc >= freshFloor).ToList();
        if (recent.Count == 0) return 0;

        // Step 2: bulk dedupe — one query per subreddit instead of one per post.
        // Note this is intentionally racy with concurrent inserts elsewhere; the
        // unique index on RedditPostId is what guarantees correctness. The bulk
        // query is just an optimisation.
        var ids = recent.Select(p => p.Id).ToList();
        HashSet<string> alreadySeen;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            alreadySeen = (await db.RedditLeads
                    .Where(l => ids.Contains(l.RedditPostId))
                    .Select(l => l.RedditPostId)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);
        }

        var fresh = recent.Where(p => !alreadySeen.Contains(p.Id)).ToList();
        if (fresh.Count == 0) return 0;

        var surfaced = 0;
        foreach (var post in fresh)
        {
            try
            {
                if (await TryProcessPostAsync(post, ct))
                    surfaced++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing post {PostId} from r/{Sub}", post.Id, sub);
            }
        }
        return surfaced;
    }

    private async Task<bool> TryProcessPostAsync(RedditPost post, CancellationToken ct)
    {
        // Step 3: text filter. Sole filter step in RSS mode — no author
        // quality floor since RSS doesn't expose karma/age.
        var textResult = LeadMatcher.MatchText(post);
        if (!textResult.Passes)
        {
            _logger.LogDebug(
                "Post {PostId} from r/{Sub} rejected at text filter: {Reason}",
                post.Id, post.Subreddit, textResult.RejectReason);
            return false;
        }

        // Step 4: persist + post.
        // AuthorAccountAgeDays + AuthorKarma stay at 0 — the embed builder
        // handles "no author meta available" gracefully. Columns retained
        // in the schema so we can wire them back up if Reddit ever re-opens
        // Data API app creation.
        var lead = new RedditLead
        {
            RedditPostId         = post.Id,
            Subreddit            = post.Subreddit,
            AuthorUsername       = post.Author,
            AuthorAccountAgeDays = 0,
            AuthorKarma          = 0,
            Title                = post.Title,
            Excerpt              = TruncateForStorage(post.Selftext),
            Url                  = post.Permalink,
            PostedAtUtc          = post.CreatedUtc,
            DiscoveredAtUtc      = DateTime.UtcNow,
            DiscordMessageId     = 0,
            Status               = LeadStatus.New,
            MatchedKeywords      = textResult.MatchedKeywords,
        };

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        try
        {
            db.RedditLeads.Add(lead);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Almost certainly a unique-index violation from a concurrent
            // insert (vanishingly unlikely with our single-loop design,
            // but cheap to handle). Treat as already-surfaced and move on.
            _logger.LogDebug(ex, "Concurrent insert for {PostId}; assuming dedupe and skipping", post.Id);
            return false;
        }

        // Now the row exists. Try to post the embed; on failure leave
        // DiscordMessageId at 0 (see class doc for rationale).
        var channel = _client.GetChannel(_options.LeadsChannelId) as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning(
                "Leads channel {ChannelId} not found or not a message channel; row {Id} saved without embed",
                _options.LeadsChannelId, lead.Id);
            return true; // we still surfaced the row, just not the embed
        }

        try
        {
            var embed = RedditLeadEmbedBuilder.Build(lead);
            var components = RedditLeadEmbedBuilder.BuildComponents(lead);
            var message = await channel.SendMessageAsync(embed: embed, components: components);

            lead.DiscordMessageId = message.Id;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Surfaced lead {Id} from r/{Sub} (u/{Author}, post {PostId})",
                lead.Id, lead.Subreddit, lead.AuthorUsername, lead.RedditPostId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to post embed for lead {Id} (post {PostId}); row stays with DiscordMessageId=0",
                lead.Id, lead.RedditPostId);
        }

        return true;
    }

    private static string TruncateForStorage(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // Storage limit slightly above the embed's 350 to give the embed
        // builder a touch of room for its own truncation marker without
        // having to re-fetch from Reddit.
        if (s.Length <= ExcerptStorageLimit + 100) return s;
        return s[..(ExcerptStorageLimit + 99)].TrimEnd() + "…";
    }
}