namespace ClanGuardBot.Briefing;

/// <summary>
/// Pre-aggregated, pre-filtered weekly snapshot sent to Claude.
///
/// COST NOTE: Every field here was filtered or aggregated in C# before
/// reaching the prompt. We send a few dozen line items, NOT the full roster
/// + attendance log. With the v2 enrichments (per-item activity context,
/// spotlight, risk watch, week-over-week deltas) target serialized size is
/// ~1.5K input tokens, up from ~600 in v1. Per-run cost lands around $0.02.
///
/// ── v3 enrichment: Recruitment Sources ──
/// RecruitmentSources + TopReferrers add ~50 input tokens for an active
/// week (negligible). Sourced from the InviteJoin table written by
/// InviteAttributionService. Both are empty when the week saw zero
/// attributable joins.
/// </summary>
public sealed record BriefingContext
{
    public required DateTime WeekStart { get; init; }
    public required DateTime WeekEnd { get; init; }

    // Top-line numbers — cheap to include, anchor the briefing

    /// <summary>Every non-bot guild member right now, Guests and exempt roles included.</summary>
    public int TotalMembers { get; init; }

    /// <summary>
    /// Non-bot, non-exempt members who hold a rank role and are not AWOL — the
    /// roster that is actually expected to show up. Also the denominator for
    /// <see cref="AverageAttendancePercent"/>.
    /// </summary>
    public required int ActiveMemberCount { get; init; }

    /// <summary>Non-bot, non-exempt members holding no rank role (Guests / not yet onboarded).</summary>
    public int GuestCount { get; init; }

    /// <summary>Members holding the AWOL role right now.</summary>
    public int AwolCount { get; init; }

    public required int EventsHeld { get; init; }
    public required double AverageAttendancePercent { get; init; }

    /// <summary>
    /// Distinct members given the RCT rank during the window as their first
    /// rank in their current stay in the server, read from the append-only
    /// RankChange log. Counts recruits who have since been promoted or have
    /// left — the log keeps them, unlike RankHistory, which only holds each
    /// current member's current rank. An RCT role removed and re-added, or a
    /// demotion back to RCT, doesn't count; a member who left and rejoined does.
    /// </summary>
    public required int NewRecruits { get; init; }

    /// <summary>
    /// Server joins in the window (InviteJoin rows). Same count /invite stats
    /// reports. This is the top of the funnel; <see cref="NewRecruits"/> is the
    /// subset that went on to take the RCT rank.
    /// </summary>
    public int TotalJoins { get; init; }

    /// <summary>Where this week's joiners stand now. Null when nobody joined.</summary>
    public IntakeSnapshot? Intake { get; init; }

    /// <summary>Week-over-week deltas vs the prior 7-day window. Null if prior-week data couldn't be computed.</summary>
    public WeekOverWeekDeltas? WeekOverWeek { get; init; }

    // Attention items — pre-filtered to top N each, capped in the collector

    /// <summary>
    /// Members currently holding the AWOL role, longest-flagged first. Capped;
    /// <see cref="AwolCount"/> is the uncapped total.
    /// </summary>
    public IReadOnlyList<AwolRiskItem> AwolRisks { get; init; } = [];

    /// <summary>Distinct members newly flagged AWOL during the window, whether or not they've since recovered.</summary>
    public int AwolFlaggedThisWeek { get; init; }

    public IReadOnlyList<PromotionCandidate> PromotionCandidates { get; init; } = [];

    /// <summary>Uncapped count behind <see cref="PromotionCandidates"/>.</summary>
    public int PromotionCandidatesTotal { get; init; }

    public IReadOnlyList<NotableEvent> NotableEvents { get; init; } = [];

    /// <summary>
    /// Members who are not AWOL today but WILL be flagged by AwolCheckService
    /// within the next 7 days if they post nothing and stay out of voice: the
    /// activity that remains in their window once the oldest week rolls off is
    /// below threshold. Officers can re-engage them before the sweep catches
    /// them. Empty if nobody is in that position.
    /// </summary>
    public IReadOnlyList<RiskWatchItem> RiskWatch { get; init; } = [];

    /// <summary>Uncapped count behind <see cref="RiskWatch"/>.</summary>
    public int RiskWatchTotal { get; init; }

    /// <summary>
    /// Most-active member of the week by combined score (messages + voice + events).
    /// Null if there isn't a clear standout (e.g. very quiet week).
    /// </summary>
    public MemberSpotlight? Spotlight { get; init; }

    /// <summary>
    /// Joins in the briefing week broken down by labeled invite source.
    /// Sorted descending by JoinCount in the collector. Empty when no joins
    /// happened (or none were attributable). Labels are the verbatim values
    /// from InviteJoin.LabelSnapshot — both real labels ("Website") and
    /// sentinels ("Vanity", "Unknown", "Ambiguous", "Unattributed") all
    /// surface here. Claude can choose to call out sentinels or skip them
    /// based on the prompt guidance.
    /// </summary>
    public IReadOnlyList<RecruitmentSourceItem> RecruitmentSources { get; init; } = [];

    /// <summary>
    /// Top members by personal-invite referrals in the week. Sourced from
    /// InviteJoin.InviterDiscordId, which Discord populates only for joins
    /// via regular invites (not vanity, not server discovery). Capped at a
    /// small N in the collector. Empty when nobody on the roster has any
    /// referred joins.
    /// </summary>
    public IReadOnlyList<TopReferrerItem> TopReferrers { get; init; } = [];

    /// <summary>
    /// Snapshot of the Reddit recruit-lead funnel for the briefing week.
    /// Null when no leads were surfaced in the window — the prompt skips
    /// the section in that case, matching the Spotlight convention.
    /// Sourced from the RedditLeads table written by RedditLeadService.
    /// </summary>
    public RedditLeadsSnapshot? RedditLeads { get; init; }

    /// <summary>
    /// Retention snapshot for the briefing week. Null when zero departures
    /// occurred — the prompt treats null as "skip the section," matching the
    /// Spotlight / RedditLeads convention. Sourced from the MemberDeparture
    /// table via BriefingRetentionSection.
    /// </summary>
    public RetentionSnapshot? Retention { get; init; }

    /// <summary>
    /// Poll engagement snapshot for the briefing week. Null when no polls were
    /// created in the window and none are currently open — the prompt treats null
    /// as "skip the section," matching the RedditLeads / Retention convention.
    /// Sourced from the Poll / PollVote tables via BriefingPollSection.
    /// </summary>
    public PollsSnapshot? Polls { get; init; }

    /// <summary>Free-form short notes for things that don't fit the other buckets.</summary>
    public IReadOnlyList<string> Anomalies { get; init; } = [];
}

/// <summary>
/// Where the window's joiners stand at briefing time. Unique joiners can be
/// lower than <see cref="BriefingContext.TotalJoins"/> when someone joined,
/// left and rejoined inside the window.
/// </summary>
/// <param name="UniqueJoiners">Distinct people behind the window's joins.</param>
/// <param name="StillInServer">Of those, how many are still in the server now.</param>
/// <param name="NowRanked">Of those still here, how many hold a rank role (onboarded).</param>
public sealed record IntakeSnapshot(
    int UniqueJoiners,
    int StillInServer,
    int NowRanked);

/// <param name="DaysSinceAwolAssigned">Days since the current AWOL flag was raised. Null when the role is held but no AwolRecord exists (applied by hand).</param>
/// <param name="Messages14d">Messages sent in the last 14 days. 0 = fully checked out.</param>
/// <param name="VoiceHours14d">Voice hours in the last 14 days. Capped per session per BotConfig.MaxSingleSessionHours.</param>
/// <param name="TrendNote">One-line context, e.g. "flagged 5d ago, grace period elapsed".</param>
public sealed record AwolRiskItem(
    string Gamertag,
    string CurrentRank,
    int? DaysSinceAwolAssigned,
    int Messages14d,
    double VoiceHours14d,
    string TrendNote);

/// <summary>
/// A member who has served the time-in-rank for their next auto-promotion
/// tier. Thresholds come from AutoPromotionService's own ladder, so this is
/// judged exactly the way the nightly job judges it.
/// </summary>
/// <param name="EventsAttendedAtRank">Cumulative events at current rank (bot-tracked + seed). Used by promotion math directly.</param>
/// <param name="MessagesAtRank">Messages sent since rank was assigned.</param>
/// <param name="VoiceHoursAtRank">Voice hours since rank was assigned (capped per session).</param>
/// <param name="RequiredEvents">Events the tier needs. 0 = message/voice tier (RCT → CPL).</param>
/// <param name="RequiredMessages">Messages the tier needs (OR voice). 0 on event tiers.</param>
/// <param name="RequiredVoiceHours">Voice hours the tier needs (OR messages). 0 on event tiers.</param>
/// <param name="MeetsActivity">True when the activity bar is met too, not just the time gate.</param>
/// <param name="AutoPromoteEnabled">False when the tier (or auto-promotion as a whole) is in dry run, so a human has to promote.</param>
public sealed record PromotionCandidate(
    string Gamertag,
    string CurrentRank,
    string ProposedRank,
    int EventsAttendedAtRank,
    int MessagesAtRank,
    double VoiceHoursAtRank,
    int DaysInRank,
    int RequiredEvents,
    int RequiredMessages,
    double RequiredVoiceHours,
    bool MeetsActivity,
    bool AutoPromoteEnabled);

/// <param name="Source">"Clan" or "CompDiv" — matches the existing event source filter.</param>
public sealed record NotableEvent(
    string EventName,
    DateTime Date,
    int Attendance,
    string Source);

/// <summary>
/// Member who will be flagged AWOL within the next 7 days unless they're
/// active: not AWOL today, but the part of their activity that will still be
/// inside their window a week from now is below threshold on both axes.
/// </summary>
/// <param name="Messages">Messages in their full window today.</param>
/// <param name="VoiceHours">Voice hours in their full window today.</param>
/// <param name="RetainedMessages">Of <paramref name="Messages"/>, how many will still count a week from now.</param>
/// <param name="RetainedVoiceHours">Of <paramref name="VoiceHours"/>, how much will still count a week from now.</param>
/// <param name="ClosestThresholdRatio">Today's best axis as a fraction of the threshold. ≥ 1.0 means safe today; below 1.0 means a join/reset guard is all that's holding the flag off.</param>
public sealed record RiskWatchItem(
    string Gamertag,
    string CurrentRank,
    int Messages,
    double VoiceHours,
    int RetainedMessages,
    double RetainedVoiceHours,
    int RequiredMessages,
    double RequiredVoiceHours,
    int WindowDays,
    double ClosestThresholdRatio);

/// <remarks>
/// The winner is chosen by an internal weighted score (1pt/msg + 5pts/voice hour
/// + 10pts/event, tunable in BriefingDataCollector), but that score is
/// deliberately NOT surfaced here — the briefing shows the raw stats only, not
/// the ranking number.
/// </remarks>
public sealed record MemberSpotlight(
    string Gamertag,
    string CurrentRank,
    int Messages,
    double VoiceHours,
    int EventsAttended);

/// <summary>
/// Week-over-week deltas. Negative numbers mean "worse than last week"
/// (fewer events, lower attendance, fewer recruits). Null fields mean the
/// prior week's data couldn't be computed (e.g. brand-new database).
/// </summary>
/// <param name="AverageAttendancePercentDelta">Percentage-point change, e.g. -12.5 means attendance dropped 12.5pp vs last week.</param>
public sealed record WeekOverWeekDeltas(
    int EventsHeldDelta,
    double AverageAttendancePercentDelta,
    int NewRecruitsDelta,
    int TotalJoinsDelta);

/// <summary>
/// Single label's recruitment count for the briefing week.
/// </summary>
/// <param name="Label">
/// The InviteJoin.LabelSnapshot value. Real labels ("Website", "Facebook")
/// surface alongside sentinel values ("Vanity", "Unknown", "Ambiguous",
/// "Unattributed") so Claude has the full picture.
/// </param>
/// <param name="JoinCount">How many joins were tagged with this label in the briefing week.</param>
public sealed record RecruitmentSourceItem(
    string Label,
    int JoinCount);

/// <summary>
/// Single referrer's count for the briefing week. Discord populates the
/// inviter only for joins via regular invites — vanity / discovery / unknown
/// joins won't credit anyone here.
/// </summary>
/// <param name="Username">Best-effort display name. Snapshotted on the InviteJoin row.</param>
/// <param name="ReferralCount">Joins in the briefing week with InviterDiscordId == this user.</param>
public sealed record TopReferrerItem(
    string Username,
    ulong DiscordUserId,
    int ReferralCount);

/// <summary>
/// Briefing-week snapshot of the Reddit recruit-lead funnel. Counts are
/// scoped to leads DISCOVERED in the briefing week (not "all leads
/// touched this week" — that would conflate weeks and confuse trend reading).
/// StaleClaimedAllTime is the deliberate exception — see field doc.
///
/// Status counts mirror the LeadStatus enum:
///   New | Claimed | Contacted | Joined | Declined | NoResponse | Skipped
/// Sum of all status counts equals TotalSurfaced (every lead has exactly
/// one status at briefing time).
/// </summary>
/// <param name="TotalSurfaced">Total leads inserted into RedditLeads in the briefing week.</param>
/// <param name="StillNew">Surfaced this week, no officer has claimed yet — still sitting in the channel.</param>
/// <param name="Claimed">Officer claimed but hasn't recorded a contact outcome yet.</param>
/// <param name="Contacted">Officer claimed and reported reaching out to the redditor.</param>
/// <param name="Joined">Lead converted to a Discord member (officer-recorded; not auto-detected).</param>
/// <param name="Declined">Lead reached out, said no thanks.</param>
/// <param name="NoResponse">Lead reached out, never heard back.</param>
/// <param name="Skipped">Officer flagged the post as not worth pursuing — useful signal on matcher tuning.</param>
/// <param name="BySubreddit">Top N producers in the briefing week, capped in the section helper. Empty when no leads.</param>
/// <param name="StaleClaimedAllTime">
/// All-time count of leads currently in Claimed status that were discovered
/// more than StaleClaimedDaysThreshold days ago. Cross-week scope on purpose
/// — the briefing is the right place to nudge officers about leads they
/// claimed weeks ago and forgot about.
/// </param>
public sealed record RedditLeadsSnapshot(
    int TotalSurfaced,
    int StillNew,
    int Claimed,
    int Contacted,
    int Joined,
    int Declined,
    int NoResponse,
    int Skipped,
    IReadOnlyList<RedditLeadSubredditBreakdown> BySubreddit,
    int StaleClaimedAllTime);

/// <param name="Subreddit">Subreddit name without the "r/" prefix, as stored on RedditLead.</param>
/// <param name="SurfacedCount">Leads surfaced from this sub in the briefing week.</param>
public sealed record RedditLeadSubredditBreakdown(
    string Subreddit,
    int SurfacedCount);

/// <summary>
/// Poll-engagement snapshot for the briefing week. All counts are scoped to polls
/// CREATED in the window except <paramref name="OpenPollsNow"/>, which is a live
/// figure (polls still accepting votes right now).
/// </summary>
/// <param name="PollsCreated">Polls started this week.</param>
/// <param name="PollsClosed">Polls that closed this week.</param>
/// <param name="NativeCount">Of this week's polls, how many were native Discord polls.</param>
/// <param name="AnonymousCount">Of this week's polls, how many were anonymous (custom embed).</param>
/// <param name="TotalVotesCast">Total votes across this week's polls (multiselect can exceed voter count).</param>
/// <param name="DistinctVoters">Distinct members who voted on any poll this week.</param>
/// <param name="AvgVotersPerPoll">Mean distinct voters per poll this week (1 dp).</param>
/// <param name="OpenPollsNow">Polls currently still open (live, cross-week).</param>
/// <param name="TopPollQuestion">The week's most-voted poll question, or null if nobody voted.</param>
/// <param name="TopPollVoters">Distinct voters on that top poll.</param>
/// <param name="TopPollKind">"native" or "anonymous" for the top poll, or null.</param>
/// <param name="ChronicNonVoters">
/// Rolling participation signal over a longer window than the briefing week
/// (see <see cref="PollChronicNonVoters"/>). Null when there weren't enough
/// polls in the window to make "chronic" meaningful, so a fresh feature or a
/// quiet stretch doesn't flag the whole roster.
/// </param>
public sealed record PollsSnapshot(
    int PollsCreated,
    int PollsClosed,
    int NativeCount,
    int AnonymousCount,
    int TotalVotesCast,
    int DistinctVoters,
    double AvgVotersPerPoll,
    int OpenPollsNow,
    string? TopPollQuestion,
    int TopPollVoters,
    string? TopPollKind,
    PollChronicNonVoters? ChronicNonVoters);

/// <summary>
/// Members who had the chance to vote but consistently don't. Computed over a
/// rolling window: the eligible set is active members (non-bot, non-exempt,
/// not AWOL) who joined before the window's first poll — so they could have
/// voted in every poll in the window — and a chronic non-voter is one who voted
/// in none of them. Counts only (and display names for the officer briefing) —
/// never anyone's actual choice, which stays hidden for anonymous polls.
/// </summary>
/// <param name="WindowDays">Lookback window the figures cover.</param>
/// <param name="PollsInWindow">Polls created in the window (the opportunity count).</param>
/// <param name="EligibleMembers">Active members present for the whole window (the denominator).</param>
/// <param name="Participants">Eligible members who voted in at least one poll.</param>
/// <param name="NonVoters">Eligible members who voted in none — the chronic set.</param>
/// <param name="ParticipationRatePct">Participants ÷ eligible, as a percentage.</param>
/// <param name="Sample">Up to a capped number of chronic non-voter display names, longest-tenured first.</param>
public sealed record PollChronicNonVoters(
    int WindowDays,
    int PollsInWindow,
    int EligibleMembers,
    int Participants,
    int NonVoters,
    double ParticipationRatePct,
    IReadOnlyList<string> Sample);