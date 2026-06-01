namespace ClanGuardBot.Models;

/// <summary>
/// One row per member departure event. A member who leaves, rejoins, and
/// leaves again produces multiple rows (keyed by Id, NOT unique on
/// Guild+User) — rejoin/boomerang behavior is itself a retention signal.
///
/// ── Lifecycle ──
/// Written by DepartureCaptureHandler on the gateway UserLeft event, then
/// finalized by the classification pipeline:
///   UserLeft  → capture row (Pending, or classified immediately if a
///               matching Kick/Ban audit entry already arrived)
///   AuditLogCreated (Kick/Ban) → reconcile a recent row, or stash a
///               breadcrumb the not-yet-arrived UserLeft will consume
///   DepartureClassificationWorker → finalize stale Pending rows to "Left"
///               (no audit entry observed inside the grace window ⇒ voluntary)
///
/// Departures missed while the bot was OFFLINE (Discord never replays
/// UserLeft) are recovered separately by MemberRosterReconciler, which writes
/// rows with DepartureDetection = "Reconciled".
///
/// ── Why rank/roles are snapshotted here ──
/// MemberLifecycleHandler deletes this user's RankHistory on the SAME
/// UserLeft event (both handlers are fire-and-forget, order not guaranteed).
/// To avoid the race, the live path reads rank/roles off the in-memory
/// SocketGuildUser — it never touches the RankHistory table that's about to
/// be deleted. The reconciler uses the rank/roles it cached in KnownMember.
///
/// ── Tenure is best-effort ──
/// Discord's UserLeft carries no JoinedAt, and the SocketGuildUser may be
/// cache-evicted by the time the event fires. JoinedAt is resolved through a
/// priority chain (see DepartureFactory.ResolveJoinedAtAsync) and the source
/// is recorded so the briefing can report coverage honestly rather than
/// implying precision we don't have.
/// </summary>
public class MemberDeparture
{
    public int Id { get; set; }

    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Discord username at departure (e.g. "gravestarr").</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Server display name / nickname at departure (e.g. "SSG.GRAVESTARR"). Falls back to Username when the member was already cache-evicted.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the departure. For "Reconciled" rows this is the detection time (the bot was offline when they actually left), so treat it as approximate.</summary>
    public DateTime DepartedAt { get; set; }

    /// <summary>
    /// Best-effort join timestamp. Null only when no source could supply one.
    /// Resolution priority is recorded in JoinedAtSource.
    /// </summary>
    public DateTime? JoinedAt { get; set; }

    /// <summary>
    /// Where JoinedAt came from, in descending confidence:
    ///   "DiscordCache"  — Discord's own join timestamp (live SocketGuildUser, or KnownMember cache) — exact
    ///   "InviteJoin"    — most recent InviteJoin.JoinedAt for this user (exact-ish)
    ///   "GuestReminder" — GuestReminder.JoinedAt
    ///   "RankHistory"   — earliest RankHistory.AssignedAt (weak lower bound)
    ///   "FirstActivity" — earliest MessageEvent/VoiceSession timestamp (lower bound)
    ///   "Unknown"       — none available; JoinedAt is null, TenureDays null
    /// </summary>
    public string JoinedAtSource { get; set; } = "Unknown";

    /// <summary>Computed tenure in days (DepartedAt - JoinedAt). Null when JoinedAt is unknown.</summary>
    public double? TenureDays { get; set; }

    /// <summary>
    /// Departure classification. Settles from Pending → one of the terminal
    /// states inside the grace window.
    ///   "Pending"          — captured, awaiting audit-log correlation
    ///   "Left"             — voluntary (no kick/ban audit entry observed)
    ///   "Kicked"           — manual officer kick (actor != bot)
    ///   "KickedAwol"       — bot AWOL sweep (actor == bot, Kick action — the only bot kick path)
    ///   "KickedAccountAge" — bot account-age gate (actor == bot, Ban action with "Account-age" reason)
    ///   "Banned"           — manual ban, or any other bot ban
    /// </summary>
    public string Classification { get; set; } = "Pending";

    /// <summary>
    /// How this departure was detected:
    ///   "Live"       — captured in real time from the UserLeft gateway event
    ///   "Reconciled" — recovered after the fact by MemberRosterReconciler
    ///                  (the bot was offline when the member left, so DepartedAt
    ///                  is the detection time and classification is best-effort
    ///                  from whatever audit-log entries survive Discord's 45-day
    ///                  retention).
    /// </summary>
    public string DepartureDetection { get; set; } = "Live";

    /// <summary>Officer or bot who performed the kick/ban. Null for voluntary leaves.</summary>
    public ulong? ActorId { get; set; }
    public string? ActorName { get; set; }

    /// <summary>Audit-log reason text, when present.</summary>
    public string? Reason { get; set; }

    /// <summary>Highest rank role name at departure, or "Guest" / "None".</summary>
    public string RankAtDeparture { get; set; } = "None";

    /// <summary>CSV of role names at departure (excl. @everyone) — same format as AwolKickAuditRecord.RolesAtKick.</summary>
    public string RolesAtDeparture { get; set; } = string.Empty;

    /// <summary>True if the member never progressed past Guest (onboarding churn).</summary>
    public bool WasGuest { get; set; }

    /// <summary>True if they held the Reserve role (changes how we read an AWOL non-kick).</summary>
    public bool HadReserve { get; set; }

    /// <summary>Lifetime message count at departure — distinguishes ghost churn from engaged churn.</summary>
    public int MessagesLifetime { get; set; }

    /// <summary>Lifetime bot-tracked events attended at departure.</summary>
    public int EventsAttendedLifetime { get; set; }

    /// <summary>True if this user had an earlier MemberDeparture row (boomerang member).</summary>
    public bool IsRejoin { get; set; }

    /// <summary>The invite LabelSnapshot they originally joined under, if attributable. Null otherwise.</summary>
    public string? JoinSourceLabel { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the pipeline finalized Classification. Null while Pending.</summary>
    public DateTime? ClassifiedAt { get; set; }
}
