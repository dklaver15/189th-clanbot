namespace ClanGuardBot.PatrolWatch;

/// <summary>
/// Strongly-typed view of the "PatrolWatch" configuration section.
/// Validated on startup via PatrolWatchServiceCollectionExtensions —
/// when Enabled is true, LfgChannelId and at least one matched game are required.
///
/// ── How "matched game" works ──
/// Discord rich presence exposes Activity.Name as a string ("Battlefield 6",
/// "Battlefield™ 6", "Arc Raiders" etc.). We match case-insensitive on a list
/// of substrings per game — different platforms sometimes register the same
/// title with subtly different strings (TM glyph, trailing version, etc.), so
/// the substring list is the hedge.
///
/// ── PS5 / Xbox visibility caveat ──
/// Members on console only broadcast a Playing activity if they've linked
/// their PSN / Xbox account to Discord (User Settings → Connections). If they
/// haven't, the bot has no way to see what they're playing. /patrol info
/// surfaces this so members understand why they don't appear on the embed.
/// </summary>
public sealed class PatrolWatchOptions
{
    public const string Section = "PatrolWatch";

    /// <summary>Master switch. When false, no event handlers register and no embeds post.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Channel ID where the rolling embed is posted. ID rather than name so
    /// a future channel rename doesn't silently break the feature.
    /// </summary>
    public ulong LfgChannelId { get; set; }

    /// <summary>
    /// Minimum number of matched-and-opted-in members in the same VC before
    /// an embed is posted. 3 by design — duos don't trigger the post.
    /// </summary>
    public int MinSquadSize { get; set; } = 3;

    /// <summary>
    /// How long to wait after the last voice / presence event before recomputing
    /// the patrol. PresenceUpdated fires constantly; debouncing collapses
    /// bursts into a single recompute + edit cycle.
    /// </summary>
    public int DebounceSeconds { get; set; } = 12;

    /// <summary>
    /// How long the "Patrol stood down" final edit lingers before the message
    /// is deleted. Short enough to keep #lfg clean, long enough that anyone
    /// reading at the moment sees the wrap-up.
    /// </summary>
    public int StoodDownLingerSeconds { get; set; } = 60;

    /// <summary>
    /// Games to match against Activity.Name. List form so adding Arc Raiders
    /// is a config push, not a redeploy.
    /// </summary>
    public List<MatchedGame> MatchedGames { get; set; } = new();

    /// <summary>
    /// Voice-channel category IDs to ignore entirely. Members in any VC under
    /// one of these categories never count toward a patrol — typically the
    /// events category, since events already have their own announcement
    /// surface and a Patrol Watch embed would just duplicate the noise.
    /// </summary>
    public List<ulong> ExcludedCategoryIds { get; set; } = new();

    /// <summary>
    /// Specific voice-channel IDs to ignore (in addition to whole-category
    /// exclusions and the guild's official AFK channel, which is auto-
    /// excluded). Useful for AFK rooms not registered as Discord's official
    /// AFK channel, "do not disturb" rooms, mod-only VCs, etc.
    /// </summary>
    public List<ulong> ExcludedChannelIds { get; set; } = new();
}

/// <summary>
/// One game to surface in the Patrol Watch embed. DisplayName is what the
/// embed shows; ActivitySubstrings is the list of Activity.Name fragments
/// that count as "playing this game."
///
/// Also serves as the canonical "games we play" record consumed by the
/// Reddit leads pipeline (LeadMatcher) via the RedditAliases field —
/// see RedditAliases doc below.
/// </summary>
public sealed class MatchedGame
{
    /// <summary>What gets shown on the embed ("Battlefield 6", "Arc Raiders").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Case-insensitive substrings to match against Activity.Name. Common
    /// pattern: include both the plain name and the TM-glyph variant, because
    /// Discord sometimes preserves the TM character verbatim from rich-
    /// presence registration.
    /// </summary>
    public List<string> ActivitySubstrings { get; set; } = new();

    /// <summary>
    /// Reddit-vernacular aliases for this game. Consumed by LeadMatcher's
    /// per-sub game filter on broad LFG subs (r/GamerPals etc.) where
    /// passing the LFG keyword check isn't enough — the post must also
    /// mention a game we play. Distinct from ActivitySubstrings because
    /// Reddit posters write "bf6" / "hd2" / "hll" while Discord rich
    /// presence never does. Acronyms welcome here.
    ///
    /// Empty list means this game contributes nothing to the Reddit filter
    /// — fine for games we patrol but don't actively recruit for.
    ///
    /// Word-boundary regex applies on the Reddit side, so short aliases
    /// like "hd2" won't match "shd24"; multi-word entries match across
    /// flexible whitespace ("hell  let  loose" → match).
    /// </summary>
    public List<string> RedditAliases { get; set; } = new();

    /// <summary>
    /// Hex color string for the embed's left accent bar when this game is
    /// the dominant match. Format "#RRGGBB" or "RRGGBB" (no alpha). Falls
    /// back to a default gold if empty or unparseable. Per-game accents
    /// help readers parse "what game is this patrol on" at a glance without
    /// reading the title.
    /// </summary>
    public string? AccentColor { get; set; }

    /// <summary>
    /// URL of an image (square works best — ~256x256) shown in the embed's
    /// top-right corner when this game is the dominant match. Game logo /
    /// box art works well. Leave empty to skip the thumbnail. Image must be
    /// reachable from Discord's CDN — common hosts: Imgur, your own CDN, or
    /// a private Discord upload (right-click → Copy Link).
    /// </summary>
    public string? ThumbnailUrl { get; set; }
}