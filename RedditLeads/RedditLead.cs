namespace ClanGuardBot.Models;

/// <summary>
/// One row per Reddit post the lead service has surfaced as a recruitment
/// candidate. Written by RedditLeadService when a new post passes the
/// LeadMatcher filter; mutated by RedditLeadButtonHandler as officers
/// claim and work the lead through to a terminal outcome.
///
/// ── Why this table exists ──
/// Reddit is the only top-of-funnel channel that's both high volume and
/// programmatically accessible. Officers can't realistically watch five
/// subreddits live, so the bot watches them, surfaces the LFG/"looking
/// for clan" posts, and keeps a structured record of what happened to
/// each one. The lifecycle columns (Status, ClaimedAt, ContactedAt) are
/// what eventually feed /leads stats and tell us which subreddits are
/// worth our attention vs. which look active but never produce members.
///
/// ── Append-only attribution, mutable lifecycle ──
/// The discovery half of this row (RedditPostId, AuthorUsername, Title,
/// Excerpt, etc.) is captured once at surfacing time and never rewritten
/// — even if Reddit edits or deletes the post afterwards, our snapshot
/// is what the officer saw. The lifecycle half (Status and the *AtUtc
/// stamps) gets updated as the officer works the lead. This split is
/// the same idea as InviteJoin.LabelSnapshot: source-side mutations
/// don't retroactively rewrite our history.
///
/// ── DiscordMessageId is the embed pointer ──
/// We store the Discord message ID of the embed we posted so the button
/// handler can edit it in place when the status changes (rather than
/// posting a new message per state transition). The message lives in
/// BotConfig.RedditLeads.LeadsChannelId; we don't store the channel ID
/// per row because moving the channel later would invalidate every row.
///
/// ── Reposts and edits ──
/// Reddit assigns each post a fresh ID, so a deleted-and-reposted LFG
/// from the same author surfaces as a new RedditLead row. Officers can
/// hit Skip on the dupe; we don't try to be clever about deduplicating
/// across post IDs since false positives there would suppress real
/// reposts from people genuinely retrying their search.
/// </summary>
public class RedditLead
{
    public int Id { get; set; }

    /// <summary>
    /// Reddit's "t3_xxxxxx" post ID with the t3_ prefix stripped — the same
    /// short alphanumeric token that appears in the post permalink. Unique
    /// across all of Reddit; the unique index on this column is what
    /// dedupes the polling loop across cycles.
    /// </summary>
    public string RedditPostId { get; set; } = string.Empty;

    public string Subreddit { get; set; } = string.Empty;
    public string AuthorUsername { get; set; } = string.Empty;

    /// <summary>
    /// Account age in days at the time we observed the post. Snapshotted
    /// rather than derived from the user lookup endpoint at render time
    /// because Reddit's /user/X/about endpoint is the throttled one and
    /// we don't want to re-call it for every embed re-render.
    /// </summary>
    public int AuthorAccountAgeDays { get; set; }

    /// <summary>Total karma (link + comment) at observation time.</summary>
    public int AuthorKarma { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// First ~300 chars of the post body. Long enough to vibe-check the
    /// lead from the embed, short enough that the officer is incentivized
    /// to click through for full context before reaching out.
    /// </summary>
    public string Excerpt { get; set; } = string.Empty;

    /// <summary>Full https URL to the Reddit post. Stored verbatim so it's click-ready.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>When the post was created on Reddit (from created_utc).</summary>
    public DateTime PostedAtUtc { get; set; }

    /// <summary>When the polling service first surfaced this post.</summary>
    public DateTime DiscoveredAtUtc { get; set; }

    /// <summary>
    /// Snowflake of the embed message we posted to the leads channel.
    /// Used by the button handler to edit the embed in place on status
    /// transitions. 0 if the Discord post failed for any reason — the
    /// row is still kept so the dedupe index works on the next cycle.
    /// </summary>
    public ulong DiscordMessageId { get; set; }

    public LeadStatus Status { get; set; } = LeadStatus.New;

    public ulong? ClaimedByDiscordUserId { get; set; }
    /// <summary>Username at claim time, snapshotted for embed footer rendering without a guild lookup.</summary>
    public string? ClaimedByUsername { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }

    public DateTime? ContactedAtUtc { get; set; }
    public DateTime? OutcomeAtUtc { get; set; }

    /// <summary>
    /// Comma-separated list of the positive keywords that triggered the
    /// match (e.g. "lfg, ps5"). Captured for two reasons: it's shown on
    /// the embed so the officer immediately sees why the bot flagged the
    /// post, and it's invaluable later when tuning the matcher — a long
    /// run of single-keyword "lfg"-only matches that all get Skipped
    /// tells you something about that keyword's signal-to-noise.
    /// </summary>
    public string MatchedKeywords { get; set; } = string.Empty;
}

/// <summary>
/// Lifecycle states for a RedditLead. Values are pinned with explicit
/// integer assignments so reordering the enum can never silently
/// renumber existing rows in the DB (HasConversion&lt;int&gt;()).
///
/// Transitions:
///   New       → Claimed | Skipped
///   Claimed   → Contacted | Joined | Declined | NoResponse | New (Unclaim)
///   Contacted → Joined | Declined | NoResponse
///   Joined / Declined / NoResponse / Skipped: terminal
/// </summary>
public enum LeadStatus
{
    New        = 1,
    Claimed    = 2,
    Contacted  = 3,
    Joined     = 4,
    Declined   = 5,
    NoResponse = 6,
    Skipped    = 7,
}
