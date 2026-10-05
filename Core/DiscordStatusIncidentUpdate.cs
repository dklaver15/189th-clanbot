namespace ClanGuardBot.Models;

/// <summary>
/// Dedupe row for the DiscordStatusMonitorService — one row per
/// individual incident_update emitted by discordstatus.com's Statuspage
/// JSON API. Each phase of an incident (investigating → identified →
/// monitoring → resolved) is its own update with its own immutable id,
/// so dedup-by-UpdateId lets us post every phase as a fresh message
/// without re-posting on every poll.
///
/// ── Why a separate table rather than a column on a "DiscordIncident" row ──
/// The Statuspage payload is hierarchical (incidents[].incident_updates[])
/// but for our purposes we only care about the leaf updates. Flattening
/// keeps the dedupe key simple (one column, primary key) and avoids
/// having to track which update IDs we've seen per incident.
///
/// ── Retention ──
/// No explicit prune today. Each row is &lt; 100 bytes; even if Discord
/// has a bad year and posts a thousand updates, that's &lt; 100KB. If the
/// table ever grows enough to matter we can prune entries older than
/// 90 days the same way CommandUsagePruneService does.
/// </summary>
public class DiscordStatusIncidentUpdate
{
    /// <summary>
    /// Statuspage incident_update id (e.g. "h2dx3qj4l8nf"). Primary key —
    /// these are globally unique strings assigned by Statuspage and never
    /// reused. Acts as the idempotency token for the post loop.
    /// </summary>
    public string UpdateId { get; set; } = string.Empty;

    /// <summary>
    /// Parent incident id. Kept for diagnostics / future "group all
    /// updates for incident X" queries; not used by the post loop.
    /// </summary>
    public string IncidentId { get; set; } = string.Empty;

    /// <summary>
    /// Statuspage status at the time we recorded the update
    /// (investigating / identified / monitoring / resolved / postmortem).
    /// Stored verbatim for diagnostics.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when ClanGuard recorded this update (NOT the
    /// Statuspage created_at). Used to distinguish "we just saw this
    /// for the first time" from "this was seeded during first-run sync".
    /// </summary>
    public DateTime PostedAt { get; set; }

    /// <summary>
    /// True if this row was inserted during the first-run seed pass
    /// (no message was posted to Discord). Lets us tell from the DB
    /// whether a given update was actually announced or just absorbed
    /// into the dedupe set on initial deploy.
    /// </summary>
    public bool Seeded { get; set; }
}
