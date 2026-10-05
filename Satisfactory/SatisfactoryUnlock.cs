namespace ClanGuardBot.Models;

/// <summary>
/// One thing the clan has completed on the Satisfactory server — a milestone
/// (schematic) or a piece of M.A.M. research — recorded the first time
/// <see cref="Services.SatisfactoryUnlockService"/> saw it marked complete.
///
/// ── Why this is persisted, when the power alerts aren't ──
/// A tripped fuse can be re-derived from a single poll: look at the server and
/// you know the answer. A completed milestone is a one-time EVENT. If the state
/// lived only in memory, every deploy would either lose whatever finished while
/// the bot was down, or re-announce the lot. The table is the difference between
/// "we announce completions" and "we announce completions unless we happened to
/// redeploy".
///
/// ── Why the seed is part of the key ──
/// <see cref="Seed"/> comes from getSessionInfo and is unique to a world. A new
/// save gets a new seed, so its completed-set starts empty and the feed seeds
/// itself silently instead of either re-announcing all of tier 1 or going
/// permanently quiet. This is the save-wipe problem from
/// <see cref="SatisfactorySession"/> solved properly rather than worked around —
/// and the clan has already wiped one save (July 2026, moving hosts).
///
/// Unique on (Seed, Kind, UnlockId): the poller upserts by that triple, so a
/// double-poll or a restart mid-write can't produce two rows for one unlock.
/// </summary>
public class SatisfactoryUnlock
{
    public int Id { get; set; }

    /// <summary>World seed from getSessionInfo. Scopes everything to one save.</summary>
    public long Seed { get; set; }

    /// <summary>"Milestone" or "Research" — see <see cref="Services.SatisfactoryUnlockKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// FRM's id for the thing: a schematic's <c>ID</c>
    /// ("Schematic_XMassTree_C") or a research node's
    /// ("BPD_ResearchTreeNode_C_33"). Stable within a save.
    /// </summary>
    public string UnlockId { get; set; } = string.Empty;

    /// <summary>
    /// Display name at the time it completed.
    ///
    /// <b>May be derived rather than reported.</b> FRM returns an EMPTY Name for
    /// some schematics (confirmed on the clan's server 2026-07-25 —
    /// Schematic_XMassTree_C has <c>"Name": ""</c>), so the poller falls back to
    /// prettifying the id. Snapshotted here so the record reads sensibly later
    /// regardless.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Tech tier, for grouping and for colouring the announcement.</summary>
    public int TechTier { get; set; }

    /// <summary>
    /// When WE first saw it complete — not when it actually completed. The API
    /// exposes no completion timestamp, so during a seed (first run on a save)
    /// this is "when the bot started watching", which is why seeded rows are
    /// never announced.
    /// </summary>
    public DateTime CompletedUtc { get; set; }
}
