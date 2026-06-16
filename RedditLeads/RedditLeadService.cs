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
///   3. LeadMatcher.MatchText — positive keyword + negative keyword + (for
///      subs in RedditLeads:GameFilteredSubs) game-keyword match.
///   4. Insert row, post embed, stamp DiscordMessageId.
///   5. Cap: stop processing if MaxLeadsPerCycle is reached for this cycle.
///
/// Previous OAuth-based pipeline had a tier-3 author quality floor that
/// fetched /user/X/about for karma + account age. RSS doesn't expose those
/// fields, so that step is gone. LeadMatcher's current tier 3 is a per-sub
/// game filter (different mechanism, same tier number); see LeadMatcher
/// class doc for the rationale. Author quality columns on RedditLead are
/// retained at 0 in case Reddit ever re-opens Data API app creation.
///
/// ── Per-cycle cap ──
/// MaxLeadsPerCycle bounds how many leads can be surfaced (DB row +
/// embed) in a single cycle, sized to recruiter follow-up capacity.
/// When the cap is hit, the cycle short-circuits — remaining posts and
/// remaining subs are skipped without DB or Discord side effects, so
/// they stay re-discoverable on the next poll via /new.rss. Sub
/// ordering is config-order, so a high-volume early sub can starve
/// later subs in a flood; see RedditLeadsOptions.MaxLeadsPerCycle doc
/// for the rationale and remediation if that bites.
///
/// ── Rejection logging ──
/// Every post that's rejected at the text filter is logged at Information
/// level (not Debug) with a consistent, greppable shape:
///   LeadMatcher rejected r/{Sub} post {PostId}: reason={Reason}, title={Title}
/// This is the audit trail for "why didn't this lead come through?"
/// investigations — `docker logs clanguard-bot | grep "LeadMatcher rejected"`
/// shows every drop with the reason and a title prefix to spot-check
/// against. Filter by reason with e.g. `grep "reason=negative keyword"`
/// or `grep "reason=no game keyword"`. Posts rejected upstream (stale,
/// already-seen, cap-skipped) aren't logged here. Cap-hit events are
/// logged separately at Information level so officers can spot floods:
/// `grep "cycle cap reached"`.
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
    /// At 60-min polling, the per-sub-per-hour ceiling is also 25 — if
    /// a sub regularly exceeds that, the oldest posts in the window will
    /// age off /new.rss before the next poll. Bump to 50 or 100 if logs
    /// show this happening.
    /// </summary>
    private const int ListingLimit = 25;

    /// <summary>
    /// Storage cap for the post body. Matches RedditLeadEmbedBuilder's
    /// ExcerptLimit so the embed never has to re-truncate a longer stored
    /// value. Stored as part of the row so a later edit/delete on Reddit
    /// can't rewrite our snapshot.
    /// </summary>
    private const int ExcerptStorageLimit = 350;

    /// <summary>
    /// Title-prefix length for rejection log lines. Long enough to make
    /// a post identifiable when scanning logs, short enough to keep log
    /// lines tractable. Anything over this is truncated with an ellipsis.
    /// </summary>
    private const int RejectionLogTitleLimit = 80;

    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;
    private readonly RedditRssClient _reddit;
    private readonly LeadMatcher _matcher;
    private readonly RedditLeadsOptions _options;
    private readonly ILogger<RedditLeadService> _logger;

    /// <summary>Round-robin start position, advanced each cycle so the cap and any
    /// rate-limiting don't always starve the same tail-end subs.</summary>
    private int _cycleStartOffset;

    public RedditLeadService(
        IServiceProvider services,
        DiscordSocketClient client,
        RedditRssClient reddit,
        LeadMatcher matcher,
        IOptions<RedditLeadsOptions> options,
        ILogger<RedditLeadService> logger)
    {
        _services = services;
        _client   = client;
        _reddit   = reddit;
        _matcher  = matcher;
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
            "RedditLeadService started (RSS mode). Polling {Count} subreddit(s) every {Min} min (cap {Cap}/cycle): {Subs}",
            subs.Count, _options.PollingIntervalMinutes, _options.MaxLeadsPerCycle, string.Join(", ", subs));

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
        var allSubs = _options.GetSubredditsList();
        if (allSubs.Count == 0) return;

        // Rotate the starting position each cycle so the cap (and any lingering
        // rate-limiting) doesn't always starve the same tail-end subs — over
        // successive cycles every sub gets first crack.
        var offset = _cycleStartOffset % allSubs.Count;
        _cycleStartOffset = (_cycleStartOffset + 1) % allSubs.Count;
        var subs = allSubs.Skip(offset).Concat(allSubs.Take(offset)).ToList();

        var freshFloor = DateTime.UtcNow.AddHours(-_options.MaxPostAgeHours);
        var cap = _options.MaxLeadsPerCycle;
        var spacing = TimeSpan.FromSeconds(Math.Max(0, _options.RequestSpacingSeconds));

        var totalSurfaced = 0;
        var rateLimited = 0;
        var capHit = false;
        var first = true;

        foreach (var sub in subs)
        {
            if (ct.IsCancellationRequested) break;

            var remaining = cap - totalSurfaced;
            if (remaining <= 0)
            {
                capHit = true;
                break;
            }

            // Space requests so we stay under Reddit's per-IP RSS limit (~1 req
            // per ~40s from a datacenter IP). No delay before the first request.
            if (!first && spacing > TimeSpan.Zero)
            {
                try { await Task.Delay(spacing, ct); }
                catch (OperationCanceledException) { break; }
            }
            first = false;

            try
            {
                var (surfaced, limited) = await ProcessSubredditAsync(sub, freshFloor, remaining, ct);
                totalSurfaced += surfaced;
                if (limited) rateLimited++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing subreddit r/{Sub}", sub);
            }
        }

        if (capHit || totalSurfaced >= cap)
        {
            // Cap reached this cycle. Skipped posts stay in /new.rss and
            // remain inside MaxPostAgeHours, so they'll be re-evaluated
            // next cycle. Greppable: `grep "cycle cap reached"`.
            _logger.LogInformation(
                "RedditLead cycle cap reached ({Cap} leads); remaining posts/subs deferred to next cycle",
                cap);
        }

        // Surface rate-limiting at Warning (the per-sub 429s are Debug). If this is
        // most of the list cycle after cycle, leads will dry up — raise spacing.
        if (rateLimited > 0)
            _logger.LogWarning(
                "RedditLead cycle: {Limited}/{Total} subreddit(s) rate-limited by Reddit (429) even after retry. If this is most of the list, raise RedditLeads:RequestSpacingSeconds.",
                rateLimited, subs.Count);

        if (totalSurfaced > 0)
            _logger.LogInformation("RedditLead cycle: {Count} new lead(s) surfaced across all subs", totalSurfaced);
    }

    private async Task<(int Surfaced, bool RateLimited)> ProcessSubredditAsync(
        string sub,
        DateTime freshFloor,
        int remainingCapacity,
        CancellationToken ct)
    {
        if (remainingCapacity <= 0) return (0, false);

        var fetch = await _reddit.GetNewPostsAsync(sub, ListingLimit, ct);
        if (fetch.Outcome == RedditFetchOutcome.RateLimited) return (0, true);

        var posts = fetch.Posts;
        if (posts.Count == 0) return (0, false);

        // Step 1: cheap recency filter before any DB work.
        var recent = posts.Where(p => p.CreatedUtc >= freshFloor).ToList();
        if (recent.Count == 0) return (0, false);

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
        if (fresh.Count == 0) return (0, false);

        var surfaced = 0;
        foreach (var post in fresh)
        {
            // Cap check is on SURFACED count, not processed count. Posts
            // that get rejected by the matcher don't count against the cap
            // — only successfully posted leads do. This is intentional:
            // the cap is sized to recruiter follow-up capacity, and rejected
            // posts produce no follow-up burden.
            if (surfaced >= remainingCapacity) break;

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
        return (surfaced, false);
    }

    private async Task<bool> TryProcessPostAsync(RedditPost post, CancellationToken ct)
    {
        // Step 3: text filter. Tier 1 (positive) + tier 2 (negative) always;
        // tier 3 (per-sub game match) only for subs listed in
        // RedditLeads:GameFilteredSubs. No author quality floor since RSS
        // doesn't expose karma/age.
        var textResult = _matcher.MatchText(post);
        if (!textResult.Passes)
        {
            // Information-level (not Debug) so the rejection trail is visible
            // in default log output. Consistent shape: post ID, sub, reason,
            // title prefix. Greppable: `grep "LeadMatcher rejected"` for the
            // full stream; `grep "reason=negative keyword"` to filter by type.
            _logger.LogInformation(
                "LeadMatcher rejected r/{Sub} post {PostId}: reason={Reason}, title={Title}",
                post.Subreddit, post.Id, textResult.RejectReason, TruncateTitleForLog(post.Title));
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

    /// <summary>
    /// Trim post titles for rejection log lines. Keeps lines scannable
    /// while preserving enough of the title to spot false negatives
    /// (real leads getting filtered) at a glance.
    /// </summary>
    private static string TruncateTitleForLog(string title)
    {
        if (string.IsNullOrEmpty(title)) return string.Empty;
        if (title.Length <= RejectionLogTitleLimit) return title;
        return title[..RejectionLogTitleLimit] + "...";
    }
}