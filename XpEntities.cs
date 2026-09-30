namespace ClanGuardBot.Models;

/// <summary>
/// Lifecycle of an <see cref="XpSeason"/>. Persisted as an int, so new values
/// can be added without a schema migration.
/// </summary>
public enum XpSeasonStatus
{
    /// <summary>Currently accruing XP. Exactly one Active season per guild.</summary>
    Active = 1,

    /// <summary>Closed. Its <see cref="XpMemberSeason"/> rows are frozen as the historical record.</summary>
    Ended = 2,

    /// <summary>
    /// Created ahead of time and not accruing anything yet. XpSeasonSchedulerService
    /// flips it to <see cref="Active"/> once <see cref="XpSeason.StartUtc"/> arrives.
    ///
    /// This exists so a season can be lined up for a sensible clock time (midnight on
    /// the 1st) without an officer having to be awake to type the command. It is NOT
    /// a second kind of active season: GetActiveSeasonAsync ignores it, so no XP
    /// accrues, no board standings are drawn and no announcements fire until the flip.
    /// At most one Scheduled season exists per guild, and one cannot be created while
    /// another season is still Active.
    /// </summary>
    Scheduled = 3,
}

/// <summary>
/// Where a chunk of XP came from. Persisted as an int (HasConversion&lt;int&gt;),
/// so appending values needs no migration.
///
/// ── Why this matters beyond bookkeeping ──
/// Every award carries its source, so <c>/xp</c> can show a member exactly where
/// their XP came from ("1,500 from ops, 70 from chat"). That transparency is the
/// whole point of the feature — a leaderboard nobody can audit just breeds
/// arguments about whether it's rigged.
/// </summary>
public enum XpSource
{
    /// <summary>Attended a clan event (an <see cref="EventAttendance"/> row exists).</summary>
    Event = 1,

    /// <summary>Attended a clan meeting (a <see cref="MeetingAttendance"/> row exists).</summary>
    Meeting = 2,

    /// <summary>RSVP'd "Going" to an event and then actually showed up.</summary>
    RsvpHonored = 3,

    /// <summary>Bonus for attending N clan events in a row without missing one.</summary>
    EventStreak = 4,

    /// <summary>Time spent in clan voice channels outside of events/meetings. One row per UTC day.</summary>
    Voice = 5,

    /// <summary>Chat messages, capped per day. One row per UTC day.</summary>
    Message = 6,

    /// <summary>Manual grant or deduction by an officer via <c>/xp-adjust</c>.</summary>
    Manual = 7,
}

/// <summary>
/// One competitive season of the clan XP ladder — the video-game kind: everyone's
/// season XP resets to zero when a new one starts, so a member who joins in month
/// eight isn't permanently buried behind two years of accumulated tenure.
///
/// ── Relationship to promotions ──
/// NONE, deliberately. XP is a pure engagement/recognition layer. Rank promotions
/// continue to run entirely off EventAttendanceHelper.CountEventsAttendedAsync and
/// AutoPromotionService, untouched by anything in this file. Nothing here should
/// ever be read by PromotionService — if that ever changes it needs to be a
/// conscious decision, not a quiet coupling.
///
/// ── What survives a reset ──
/// Season XP resets; <see cref="XpMemberTotal.AllTimeXp"/> does not. The ledger
/// (<see cref="XpAward"/>) is never deleted either, so any past season can be
/// recomputed or audited after the fact.
///
/// Exactly one season per guild is <see cref="XpSeasonStatus.Active"/> at a time;
/// XpService enforces that when opening a new one.
/// </summary>
public class XpSeason
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>Human-facing season number, starting at 1. Rendered as "Season 3".</summary>
    public int Number { get; set; }

    /// <summary>
    /// Optional flavour name shown after the number ("Season 3: Iron Vanguard").
    /// Empty = just "Season 3".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the season opened. XP earned before this instant belongs to an earlier season.</summary>
    public DateTime StartUtc { get; set; }

    /// <summary>
    /// The end date the board shows. Set from BotConfig.XpSeasonLengthDays (or the
    /// command's days: option) at open time, or to the exact instant an officer
    /// scheduled a close for.
    ///
    /// Whether this is a deadline or just a target depends entirely on
    /// <see cref="AutoEndUtc"/>. With AutoEndUtc null this is DISPLAY ONLY, nothing
    /// acts on it, and past the date the board reads "past its planned end" rather
    /// than pretending the season closed itself. Null = no date shown at all.
    /// </summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>
    /// When set, XpSeasonSchedulerService closes the season at this instant: final
    /// standings locked in, results card posted, XP stops accruing. Null means the
    /// season only ends when an officer runs /xp-season end, which is how every
    /// season behaved before scheduling existed.
    ///
    /// INVARIANT: whenever this is non-null it equals <see cref="EndUtc"/>. Two
    /// dates that can disagree is exactly how a board ends up counting down to one
    /// instant and closing at another, so both are only ever written together, by
    /// XpService. Read AutoEndUtc to decide whether to act; read EndUtc to display.
    /// </summary>
    public DateTime? AutoEndUtc { get; set; }

    /// <summary>When the season was actually closed (may differ from <see cref="EndUtc"/>). Null while Active.</summary>
    public DateTime? ClosedUtc { get; set; }

    public XpSeasonStatus Status { get; set; } = XpSeasonStatus.Active;

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// How many of the pre-start reminders (one week, one day, six hours; see
    /// XpSeasonReminders) have been dealt with for this Scheduled season, 0 to 3, in
    /// order. "Dealt with" means sent, or skipped because its time had already
    /// passed when the season was scheduled or a later reminder was already due.
    /// Persisted so a restart never repeats an @here.
    /// </summary>
    public int PreStartRemindersSent { get; set; }
}

/// <summary>
/// A single immutable-ish grant of XP: the ledger. Every point a member holds is
/// traceable to one of these rows.
///
/// ── Idempotency is the whole design ──
/// XpAccrualService does not stream events off the gateway; it periodically
/// re-derives XP from tables the bot already maintains (EventAttendances,
/// MeetingAttendances, VoiceSessions, MessageEvents) over a rolling lookback
/// window. That means the SAME underlying activity gets looked at on many
/// consecutive cycles, and the accrual pass must be safe to re-run forever
/// without double-paying.
///
/// <see cref="SourceKey"/> plus <see cref="Source"/> is what makes that safe. It
/// is a stable, deterministic identifier for the thing being paid for:
///
///   Event        "evt:{calendarEventId}"     — one payout per member per event
///   Meeting      "meet:{calendarEventId}"
///   RsvpHonored  "rsvp:{calendarEventId}"
///   EventStreak  "streak3:{calendarEventId}" / "streak5:{calendarEventId}"
///   Voice        "voice:{seasonId}:{yyyy-MM-dd}"  — one ROLLUP row per member per UTC day
///   Message      "msg:{seasonId}:{yyyy-MM-dd}"    — one ROLLUP row per member per UTC day
///                (season-scoped: without the id, a day straddling a season boundary
///                 is claimed by whichever season got there first and the new one
///                 never pays it)
///   Manual       "manual:{guid}"             — always unique; officers can stack grants
///
/// A unique index on (GuildId, UserId, Source, SourceKey) is the structural
/// backstop. Per-event rows are insert-once. The two daily rollups are UPSERTS —
/// the accrual recomputes the whole day from source and overwrites Amount, which
/// is why the per-day caps stay correct even if a cycle is missed, runs twice, or
/// the bot is down for an afternoon.
/// </summary>
public class XpAward
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }

    /// <summary>The <see cref="XpSeason"/> this award counted toward. Not a FK, for the same reasons EventAttendance isn't.</summary>
    public int SeasonId { get; set; }

    public ulong UserId { get; set; }

    public XpSource Source { get; set; }

    /// <summary>Deterministic dedup key — see the class remarks for the format per source.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>
    /// XP granted. Signed: <see cref="XpSource.Manual"/> deductions are negative,
    /// so the ledger stays append-only and a correction is visible rather than
    /// silently rewriting history.
    /// </summary>
    public int Amount { get; set; }

    /// <summary>Free-text detail: the streak length, the minutes of voice, or an officer's reason for a manual grant.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// When the underlying activity happened (event end, the day being rolled up,
    /// the moment of a manual grant) — NOT when the row was written. Season
    /// attribution and the /xp breakdown both key off this.
    /// </summary>
    public DateTime EarnedAtUtc { get; set; }

    /// <summary>When the accrual actually wrote/last-updated this row.</summary>
    public DateTime AwardedAtUtc { get; set; }
}

/// <summary>
/// Per-member, per-season rollup — the leaderboard reads exclusively from here.
///
/// Strictly derived: it is always the sum of that member's <see cref="XpAward"/>
/// rows for the season, and XpService recomputes it from the ledger rather than
/// incrementing it, so a bug in one accrual cycle can't permanently skew a total.
/// It exists purely so the board doesn't have to aggregate the whole ledger every
/// refresh.
///
/// Rows for ended seasons are left in place as the historical record.
/// </summary>
public class XpMemberSeason
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public int SeasonId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>
    /// Display-name snapshot, refreshed on each accrual. Same rationale as
    /// EventAttendance.Username — it's the fallback label for a member who has
    /// since left the guild, where a &lt;@id&gt; mention renders as a dead link.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Season XP. Never negative — a manual deduction below zero floors here.</summary>
    public int Xp { get; set; }

    /// <summary>Level derived from <see cref="Xp"/> via XpService.LevelForXp. Denormalized for sorting/display.</summary>
    public int Level { get; set; } = 1;

    /// <summary>
    /// Highest level we've already announced for this member this season — a
    /// high-water mark, same trick FinalsLeaderboardService uses for rank-ups.
    /// Persisted (not in-memory) so a restart never re-announces, and never
    /// lowered, so a manual XP deduction doesn't cause a re-announce when they
    /// climb back over the line.
    /// </summary>
    public int LastAnnouncedLevel { get; set; } = 1;

    /// <summary>Most recent EarnedAtUtc across this member's awards — used as the leaderboard tiebreaker (earlier to the total wins).</summary>
    public DateTime LastEarnedUtc { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Per-member lifetime totals that deliberately survive every season reset.
///
/// This is the answer to "if it all wipes every three months, why bother?" — the
/// season board is the competitive surface, this is the permanent one. /xp shows
/// both.
/// </summary>
public class XpMemberTotal
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Username { get; set; } = string.Empty;

    /// <summary>Sum of every award across every season. long, because this is designed to keep climbing for years.</summary>
    public long AllTimeXp { get; set; }

    /// <summary>How many seasons this member has earned any XP in.</summary>
    public int SeasonsPlayed { get; set; }

    /// <summary>Their best single-season XP total, and which season it was.</summary>
    public int BestSeasonXp { get; set; }

    public int BestSeasonNumber { get; set; }

    /// <summary>Best finishing place on any season leaderboard (1 = won a season). 0 = never placed.</summary>
    public int BestSeasonPlace { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A member who has asked not to receive level-up DMs.
///
/// ── Why a row per opt-out rather than a flag per member ──
/// The overwhelming majority of members will never opt out, so a table that only
/// holds the exceptions stays tiny and needs no backfill — absence of a row means
/// "still opted in", which is the correct default for a feature nobody asked to
/// be enrolled in but everybody benefits from by default.
///
/// Written by the "Stop these DMs" button on the DM itself, and by
/// <c>/xp-dms</c>. Deleted when they turn DMs back on, so the table only ever
/// contains people who currently want silence.
///
/// Scoped per guild for consistency with every other XP table, even though the
/// bot serves one clan today.
/// </summary>
public class XpDmOptOut
{
    public int Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }

    /// <summary>When they opted out. Kept for support questions ("I never turned these off").</summary>
    public DateTime OptedOutAtUtc { get; set; }
}
