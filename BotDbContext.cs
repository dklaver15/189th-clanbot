using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClanGuardBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<UserActivity>        UserActivities      => Set<UserActivity>();
    public DbSet<AwolRecord>          AwolRecords         => Set<AwolRecord>();
    public DbSet<AwolKickAuditRecord> AwolKickAudits      => Set<AwolKickAuditRecord>();
    public DbSet<SecurityAuditRecord> SecurityAuditRecords => Set<SecurityAuditRecord>();
    public DbSet<AccountAgeGateExemption> AccountAgeGateExemptions => Set<AccountAgeGateExemption>();
    public DbSet<WebhookSnapshot>     WebhookSnapshots    => Set<WebhookSnapshot>();
    public DbSet<MessageEvent>        MessageEvents       => Set<MessageEvent>();
    public DbSet<VoiceSession>        VoiceSessions       => Set<VoiceSession>();
    public DbSet<RankHistory>         RankHistories       => Set<RankHistory>();
    public DbSet<RankChange>          RankChanges         => Set<RankChange>();
    public DbSet<TicketReminder>      TicketReminders     => Set<TicketReminder>();
    public DbSet<GuestReminder>       GuestReminders      => Set<GuestReminder>();
    public DbSet<OnboardingReminder>  OnboardingReminders => Set<OnboardingReminder>();
    public DbSet<CalendarEvent>       CalendarEvents      => Set<CalendarEvent>();
    public DbSet<EventAttendance>     EventAttendances    => Set<EventAttendance>();
    public DbSet<MeetingAttendance>   MeetingAttendances  => Set<MeetingAttendance>();
    public DbSet<BotState>            BotStates           => Set<BotState>();
    public DbSet<BumpState>           BumpStates          => Set<BumpState>();
    public DbSet<ApolloMessageLog>    ApolloMessageLogs   => Set<ApolloMessageLog>();
    public DbSet<ApolloEvent>         ApolloEvents        => Set<ApolloEvent>();
    public DbSet<InviteSource>        InviteSources       => Set<InviteSource>();
    public DbSet<InviteJoin>          InviteJoins         => Set<InviteJoin>();
    public DbSet<RedditLead>          RedditLeads         => Set<RedditLead>();
    public DbSet<CommandUsage>        CommandUsages       => Set<CommandUsage>();
    public DbSet<PatrolWatchOptOut>   PatrolWatchOptOuts  => Set<PatrolWatchOptOut>();
    public DbSet<PatrolWatchNameOverride> PatrolWatchNameOverrides => Set<PatrolWatchNameOverride>();
    public DbSet<CalendarOutbox>      CalendarOutbox      => Set<CalendarOutbox>();
    public DbSet<OfficerApplication>  OfficerApplications => Set<OfficerApplication>();
    public DbSet<DiscordStatusIncidentUpdate> DiscordStatusIncidentUpdates => Set<DiscordStatusIncidentUpdate>();
    public DbSet<MeetingRecording>    MeetingRecordings   => Set<MeetingRecording>();
    public DbSet<MemberDeparture>     MemberDepartures    => Set<MemberDeparture>();
    public DbSet<KnownMember>         KnownMembers        => Set<KnownMember>();
    // ── In-house events ──
    public DbSet<ClanEvent>           ClanEvents          => Set<ClanEvent>();
    public DbSet<ClanEventSeries>     ClanEventSeries     => Set<ClanEventSeries>();
    public DbSet<ClanEventTemplate>   ClanEventTemplates  => Set<ClanEventTemplate>();
    public DbSet<EventRsvp>           EventRsvps          => Set<EventRsvp>();
    public DbSet<UserTimeZone>        UserTimeZones       => Set<UserTimeZone>();
    public DbSet<Poll>                Polls               => Set<Poll>();
    public DbSet<PollOption>          PollOptions         => Set<PollOption>();
    public DbSet<PollVote>            PollVotes           => Set<PollVote>();
    // ── Support tickets ──
    public DbSet<SupportTicket>        SupportTickets        => Set<SupportTicket>();
    public DbSet<SupportTicketMessage> SupportTicketMessages => Set<SupportTicketMessage>();
    // ── Palworld server ──
    public DbSet<PalworldSession>      PalworldSessions      => Set<PalworldSession>();
    public DbSet<PalworldLink>         PalworldLinks         => Set<PalworldLink>();
    public DbSet<PalworldMetricSample> PalworldMetricSamples => Set<PalworldMetricSample>();

    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MemberDeparture>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.DepartedAt });                   // weekly briefing query
            entity.HasIndex(e => new { e.GuildId, e.UserId, e.DepartedAt });         // rejoin lookup + audit reconcile
            entity.HasIndex(e => new { e.GuildId, e.Classification, e.DepartedAt }); // classification filters
        });

        modelBuilder.Entity<KnownMember>(entity =>
        {
            // One row per currently-present member; the roster reconciler diffs
            // this against the live guild to detect departures missed offline.
            entity.HasIndex(e => new { e.GuildId, e.UserId }).IsUnique();
        });

        modelBuilder.Entity<UserActivity>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.UserId }).IsUnique();
        });

        modelBuilder.Entity<AwolRecord>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.UserId });
            entity.HasIndex(e => new { e.NotificationSent, e.AssignedAt });
        });

        modelBuilder.Entity<AwolKickAuditRecord>(entity =>
        {
            // Primary lookup: "show me all kick events in this guild, newest first"
            entity.HasIndex(e => new { e.GuildId, e.ProcessedAt });

            // Per-user history: "did we ever kick this user before, and when?"
            // Useful when a former member wants to come back and we need to
            // check whether they were kicked recently and why.
            entity.HasIndex(e => new { e.GuildId, e.UserId, e.ProcessedAt });

            // Per-invoker audit: "what kick runs has this officer initiated?"
            entity.HasIndex(e => new { e.InvokerId, e.ProcessedAt });
        });

        // ── Server-protection audit log ──────────────────────────────
        // Append-only audit row per action taken by the server-protection
        // feature set (account-age gate today; raid shield / impersonation
        // check / webhook audit / token-grabber scanner planned). Three
        // indexes cover the expected read patterns:
        //   • (GuildId, OccurredAt) — "what happened recently?" timeline scan.
        //   • (GuildId, Feature, OccurredAt) — "what did AccountAgeGate
        //     do this week?" per-feature drill-down.
        //   • (GuildId, UserId, OccurredAt) — "has this user tripped any
        //     security feature before?" per-user history. Filtered index
        //     skips the non-user rows (UserId IS NULL) so the index
        //     stays small as webhook / channel-scoped features start
        //     writing rows.
        modelBuilder.Entity<SecurityAuditRecord>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.OccurredAt });
            entity.HasIndex(e => new { e.GuildId, e.Feature, e.OccurredAt });
            entity.HasIndex(e => new { e.GuildId, e.UserId, e.OccurredAt })
                  .HasFilter("\"UserId\" IS NOT NULL");
        });

        // ── Account-age gate exemptions ───────────────────────────────
        // One-time allowlist entries added by /allow-new-account and consumed
        // by AccountAgeGateHandler on the exempted user's next join. Unique on
        // (GuildId, UserId): a user is cleared or not — there's no meaning to
        // two rows. The command upserts on this key; the index is the backstop.
        modelBuilder.Entity<AccountAgeGateExemption>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.UserId }).IsUnique();
        });

        modelBuilder.Entity<WebhookSnapshot>(entity =>
        {
            // (GuildId, WebhookId) unique — WebhookId is the diff key and is
            // globally unique across Discord, but compound with GuildId makes
            // queries clean and gives us multi-guild safety for free.
            entity.HasIndex(e => new { e.GuildId, e.WebhookId }).IsUnique();
            // Listing by channel is the /webhook-audit hot path.
            entity.HasIndex(e => new { e.GuildId, e.ChannelId });
        });

        modelBuilder.Entity<MessageEvent>(e =>
        {
            e.HasIndex(m => new { m.GuildId, m.UserId, m.Timestamp });
        });

        modelBuilder.Entity<VoiceSession>(e =>
        {
            e.HasIndex(v => new { v.GuildId, v.UserId, v.JoinedAt });
            e.HasIndex(v => new { v.GuildId, v.UserId, v.ChannelId });
        });

        modelBuilder.Entity<RankHistory>(e =>
        {
            e.HasIndex(r => new { r.GuildId, r.UserId }).IsUnique();
        });

        // Append-only rank-change log. Two read patterns:
        //   • /timeline drill-down — every change for one (guild, user),
        //     newest-first. Covered by (GuildId, UserId, ChangedAt).
        //   • Future cross-member analytics — recent changes in a guild
        //     (e.g. "everyone promoted this week"). Covered by
        //     (GuildId, ChangedAt). Cheap to add now; carries no
        //     marginal cost until something queries it.
        modelBuilder.Entity<RankChange>(e =>
        {
            e.HasIndex(c => new { c.GuildId, c.UserId, c.ChangedAt });
            e.HasIndex(c => new { c.GuildId, c.ChangedAt });
        });

        modelBuilder.Entity<TicketReminder>(e =>
        {
            e.HasIndex(t => t.ChannelId).IsUnique();
        });

        modelBuilder.Entity<GuestReminder>(e =>
        {
            e.HasIndex(g => new { g.GuildId, g.UserId }).IsUnique();
        });

        modelBuilder.Entity<OnboardingReminder>(e =>
        {
            e.HasIndex(o => new { o.GuildId, o.UserId }).IsUnique();
        });

        modelBuilder.Entity<CalendarEvent>(e =>
        {
            // Filtered unique index on DiscordMessageId. Replaces the previous
            // non-unique IX_CalendarEvents_DiscordMessageId. Prevents duplicate
            // inserts for the same Apollo message (DiscordMessageId != 0) while
            // allowing CompDiv events (DiscordMessageId = 0) to share that
            // sentinel value freely.
            //
            // This is the backstop for the per-message lock in
            // ApolloEventHandler — see _messageLocks comment there for the
            // 2026-05-04 / 2026-05-05 race this fixes. The lock prevents the
            // race in normal operation; this constraint is what keeps a future
            // code path that bypasses the lock from ever silently reintroducing
            // duplicate rows.
            e.HasIndex(c => c.DiscordMessageId)
                .IsUnique()
                .HasFilter("\"DiscordMessageId\" <> 0")
                .HasDatabaseName("IX_CalendarEvents_DiscordMessageId_Unique");

            // Look up all events for a guild by source (Clan vs CompDiv)
            e.HasIndex(c => new { c.GuildId, c.Source });

            // Backs the parser worker's expiry sweep:
            // "events whose rebind grace window has elapsed". Almost always
            // empty, so this stays tiny and cheap to scan.
            e.HasIndex(c => c.PendingCancelUntil)
                .HasDatabaseName("IX_CalendarEvents_PendingCancelUntil");

            // Backs the /sort rebind lookup (match a re-post to a pending-cancel
            // event by content) and the reconciler's downtime rebind.
            e.HasIndex(c => new { c.GuildId, c.ContentHash })
                .HasDatabaseName("IX_CalendarEvents_GuildId_ContentHash");
        });

        modelBuilder.Entity<CalendarOutbox>(e =>
        {
            // Drives the worker's "next batch to process" query — pending rows
            // ordered by when they're due. Filtered to (CompletedAt, NextAttemptAt)
            // because most queries look only at pending rows, but we keep
            // CompletedAt as the leading column so the index also supports
            // completed-row lookups for diagnostics.
            e.HasIndex(o => new { o.CompletedAt, o.NextAttemptAt })
                .HasDatabaseName("IX_CalendarOutbox_Pending");

            // Investigative lookups: "show me all queue activity for this event".
            e.HasIndex(o => o.CalendarEventId);

            // Operation is stored as int — SQLite has no native enum type.
            e.Property(o => o.Operation).HasConversion<int>();
        });

        modelBuilder.Entity<EventAttendance>(e =>
        {
            // One attendance row per user per event. The snapshot service uses
            // this to avoid double-counting if a sweep runs twice for the same
            // event (e.g. due to restart + catch-up overlap).
            e.HasIndex(a => new { a.GuildId, a.UserId, a.CalendarEventId }).IsUnique();

            // Primary query pattern from EventAttendanceHelper: "how many events
            // has this user attended in a time window?"
            e.HasIndex(a => new { a.GuildId, a.UserId, a.EventEndUtc });

            // Sweep query: "events that ended recently but haven't been snapshotted"
            e.HasIndex(a => a.CalendarEventId);
        });

        // ── Meeting attendance ────────────────────────────────────────
        // Mirror of EventAttendance for meeting-VC qualifying sessions.
        // Same three indexes serve identical access patterns:
        //   • Unique (GuildId, UserId, CalendarEventId) keeps the snapshot
        //     service idempotent across restart + catch-up overlap.
        //   • (GuildId, UserId, EventEndUtc) backs the helper's window count.
        //   • CalendarEventId backs the sweep's "any row for this event yet?"
        //     existence check.
        // Tables are kept structurally separate so the event-side and the
        // meeting-side snapshot pipelines can run independently against the
        // same CalendarEvent rows without colliding on the unique constraint.
        modelBuilder.Entity<MeetingAttendance>(e =>
        {
            e.HasIndex(a => new { a.GuildId, a.UserId, a.CalendarEventId }).IsUnique();
            e.HasIndex(a => new { a.GuildId, a.UserId, a.EventEndUtc });
            e.HasIndex(a => a.CalendarEventId);
        });

        // ── Meeting recordings ────────────────────────────────────────
        // One row per recorded meeting occurrence (keyed in practice by the
        // Apollo DiscordMessageId). DiscordMessageId index backs the scheduler's
        // per-occurrence upsert/de-dupe; State index backs the "find rows in
        // state X to drive" sweeps. No unique constraint — the scheduler
        // enforces single-active-row-per-message in code so it can distinguish
        // active states from terminal ones (Posted/Pruned/Cancelled/Failed).
        modelBuilder.Entity<MeetingRecording>(e =>
        {
            e.HasIndex(m => m.DiscordMessageId);
            e.HasIndex(m => m.State);
        });

        // BotState is a singleton table — only one row, no indexes needed.
        // The primary key on Id is sufficient.

        modelBuilder.Entity<BumpState>(e =>
        {
            // One row per guild — keyed by GuildId for upsert semantics in
            // BumpReminderHandler.HandleBumpSuccessAsync.
            e.HasIndex(b => b.GuildId).IsUnique();
        });

        // ── Phase 1: Apollo lossless capture ──────────────────────────
        // Append-only log of every Apollo message Discord delivers. Written
        // by ApolloMessageCaptureHandler. The unique index on
        // (DiscordMessageId, RevisionNumber) is the structural backstop
        // for the per-message lock in that handler — same belt-and-
        // suspenders pattern as IX_CalendarEvents_DiscordMessageId_Unique
        // above. The (ProcessedAt, CapturedAt) index drives the parser
        // worker's "next batch of unprocessed rows" query.
        modelBuilder.Entity<ApolloMessageLog>(e =>
        {
            e.ToTable("ApolloMessageLog");
            e.Property(x => x.EventType).HasConversion<int>();

            e.HasIndex(x => new { x.DiscordMessageId, x.RevisionNumber }).IsUnique();
            e.HasIndex(x => new { x.ProcessedAt, x.CapturedAt });
            e.HasIndex(x => x.CapturedAt);
        });

        // ── Phase 2: Apollo parsed event staging ──────────────────────
        // Written by ApolloMessageParserWorker. Shadow of CalendarEvent
        // for clan-sourced events during dual-run; Phase 3 cutover
        // retires this table and has the worker write to CalendarEvent
        // directly. Unique index on DiscordMessageId enforces the
        // "one row per Apollo message, regardless of how many revisions
        // arrived" upsert semantics.
        modelBuilder.Entity<ApolloEvent>(e =>
        {
            e.ToTable("ApolloEvents");
            e.Property(x => x.Status).HasConversion<int>();

            e.HasIndex(x => x.DiscordMessageId).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.Status, x.ParsedStartUtc });
        });

        // ── Tracked invite links ──────────────────────────────────────
        // Two tables: InviteSource is the labeled-invite registry written
        // by /invite create and /invite assign; InviteJoin is the append-
        // only attribution log written on every UserJoined event observed
        // by InviteAttributionService. See the entity XML docs for the
        // full design rationale (label snapshotting, sentinel values for
        // unattributed joins, etc).
        modelBuilder.Entity<InviteSource>(e =>
        {
            // Code is globally unique at Discord's level but we still scope
            // the unique index to (GuildId, Code) so a future multi-guild
            // deployment can't accidentally collide.
            e.HasIndex(s => new { s.GuildId, s.Code }).IsUnique();

            // Drives /invite list "show me my live labeled invites for this guild".
            e.HasIndex(s => new { s.GuildId, s.IsActive });

            // Drives the "top referrers" briefing query and any future
            // "what links did this officer create" lookup.
            e.HasIndex(s => s.CreatedByDiscordId);
        });

        modelBuilder.Entity<InviteJoin>(e =>
        {
            // Primary briefing query: joins in (guild, time window) grouped by label.
            e.HasIndex(j => new { j.GuildId, j.JoinedAt });

            // /invite info drill-down: joins for a specific code.
            e.HasIndex(j => new { j.GuildId, j.InviteCode });

            // Future "this user's join history" lookup if we ever need it.
            e.HasIndex(j => j.UserDiscordId);
        });

        // ── Reddit recruitment leads ──────────────────────────────────
        // Written by RedditLeadService when a Reddit post passes LeadMatcher,
        // mutated by RedditLeadButtonHandler as officers work the lead.
        // The unique index on RedditPostId is the structural dedupe for
        // the polling loop — same belt-and-suspenders pattern as the
        // ApolloMessageLog and CalendarEvent unique indexes. The polling
        // service does a bulk-existence query to filter listings before
        // hitting the DB; this constraint is the floor that keeps any
        // future code path that bypasses that query from silently
        // double-inserting.
        modelBuilder.Entity<RedditLead>(e =>
        {
            e.Property(x => x.Status).HasConversion<int>();

            e.HasIndex(l => l.RedditPostId).IsUnique();

            // Drives the polling service's bulk-dedupe query and the
            // /leads recent listing.
            e.HasIndex(l => l.DiscoveredAtUtc);

            // Drives /leads stats per-subreddit grouping.
            e.HasIndex(l => l.Subreddit);

            // Drives /leads stats funnel queries ("how many in Claimed
            // state right now?") and the future "stale claimed for >Nd"
            // nag query if we ever add it.
            e.HasIndex(l => l.Status);
        });

        // ── Slash-command usage log ───────────────────────────────────
        // Append-only log of every slash-command invocation, written by
        // CommandUsageTrackingHandler and pruned to a 90-day window by
        // CommandUsagePruneService. Indexes are tuned for the two read
        // patterns we expect today (per-user history, per-command
        // popularity) plus the prune cutoff scan.
        modelBuilder.Entity<CommandUsage>(e =>
        {
            // Drives the nightly retention prune (CommandUsagePruneService).
            e.HasIndex(c => c.ExecutedAt);

            // "What has this user run lately?" — the most likely ad-hoc
            // query when investigating whether a member is engaging with
            // bot tooling.
            e.HasIndex(c => new { c.GuildId, c.UserId, c.ExecutedAt });

            // "Who's been using /command-catalog this month?" — supports
            // per-command leaderboards without scanning the whole table.
            e.HasIndex(c => new { c.GuildId, c.CommandName, c.ExecutedAt });
        });

        // ── Patrol Watch opt-outs ─────────────────────────────────────
        // Per-member toggle, written by /patrol off and removed by /patrol on.
        // Row present = opted out. Read on every recompute by PatrolWatchService
        // to filter the matched-member set before posting / editing the embed.
        modelBuilder.Entity<PatrolWatchOptOut>(e =>
        {
            // Drives the "is this member opted out?" lookup. Unique because we
            // never want two opt-out rows for the same (guild, user) — /patrol off
            // checks for an existing row before inserting, but the index is the
            // structural backstop.
            e.HasIndex(o => new { o.GuildId, o.UserId }).IsUnique();
        });

        // ── Patrol Watch name overrides ───────────────────────────────
        // One canonical display name per (guild, member), set by /patrol-name and
        // read on every recompute alongside the opt-out query. Unique on
        // (GuildId, UserId): the command upserts on this key, so a member has at
        // most one override; the index is the structural backstop.
        modelBuilder.Entity<PatrolWatchNameOverride>(e =>
        {
            e.HasIndex(o => new { o.GuildId, o.UserId }).IsUnique();
        });

        // ── Officer Applications ──────────────────────────────────────
        // One row per /apply modal submission. Written when a member submits
        // the officer application modal (Phase 2); status flips on HQ review
        // (Phase 3). Status is stored as int because SQLite has no native
        // enum type — same pattern as CalendarOutboxOperation, RedditLead.Status,
        // and ApolloEvent.Status. The (GuildId, UserId, Status) index drives
        // the Phase 2 duplicate-rejection check ("does a Pending row already
        // exist for this user?") without scanning the table.
        modelBuilder.Entity<OfficerApplication>(e =>
        {
            e.Property(a => a.Status).HasConversion<int>();
            e.HasIndex(a => new { a.GuildId, a.UserId, a.Status });
        });

        // ── Discord Status Monitor dedupe ─────────────────────────────
        // One row per incident_update id emitted by discordstatus.com.
        // UpdateId is the natural primary key (globally unique string
        // assigned by Statuspage). IncidentId index supports the future
        // "show all updates for incident X" lookup if /status ever
        // needs it; not used by the post loop itself.
        modelBuilder.Entity<DiscordStatusIncidentUpdate>(e =>
        {
            e.HasKey(x => x.UpdateId);
            e.Property(x => x.UpdateId).HasMaxLength(64);
            e.Property(x => x.IncidentId).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.HasIndex(x => x.IncidentId);
        });

        // ── In-house events ──
        modelBuilder.Entity<ClanEvent>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.StartUtc });             // list + recurrence/reminder scans
            entity.HasIndex(e => new { e.Status, e.StartUtc });              // reminder worker: Scheduled & future
            // Filtered unique index: a real Discord post has a unique MessageId,
            // but recurring-series occurrences are now materialized WITHOUT a post
            // (MessageId = 0) until they become the next-up occurrence, so many 0s
            // must be allowed to coexist. Mirrors CalendarEvents.DiscordMessageId.
            entity.HasIndex(e => e.MessageId)
                  .IsUnique()
                  .HasFilter("\"MessageId\" <> 0")
                  .HasDatabaseName("IX_ClanEvents_MessageId_Unique");        // RSVP button → event lookup
            entity.HasIndex(e => e.CalendarEventId);                         // map back to the hub row
            entity.HasIndex(e => new { e.SeriesId, e.StartUtc }).IsUnique(); // recurrence idempotency (NULLs distinct)
        });

        modelBuilder.Entity<EventRsvp>(entity =>
        {
            entity.HasIndex(e => new { e.ClanEventId, e.UserId }).IsUnique(); // one RSVP per member per event
            entity.HasIndex(e => e.ClanEventId);                             // embed render query
        });

        modelBuilder.Entity<ClanEventSeries>(entity =>
        {
            entity.HasIndex(e => new { e.GuildId, e.Active });
        });

        modelBuilder.Entity<ClanEventTemplate>(entity =>
        {
            // Listing/picking is always scoped to the guild. Name uniqueness is
            // enforced case-insensitively in the save flow (see ClanEventTemplate.Name).
            entity.HasIndex(e => e.GuildId);
        });

        modelBuilder.Entity<UserTimeZone>(entity =>
        {
            entity.HasIndex(e => e.UserId).IsUnique();
        });

        // ── Polls (/poll: native + anonymous hybrid) ──
        modelBuilder.Entity<Poll>(entity =>
        {
            entity.HasIndex(e => e.MessageId).IsUnique();        // vote/button + gateway-event → poll lookup
            entity.HasIndex(e => new { e.Status, e.ClosesAtUtc }); // close sweep: Open polls due to close
            entity.HasIndex(e => new { e.GuildId, e.CreatedAt }); // analytics / listing
        });

        modelBuilder.Entity<PollOption>(entity =>
        {
            entity.HasIndex(e => new { e.PollId, e.Position }).IsUnique(); // stable display order
            entity.HasIndex(e => new { e.PollId, e.AnswerId });            // native answer-id → option
        });

        modelBuilder.Entity<PollVote>(entity =>
        {
            // One row per (poll, user, option): re-clicking the same option is a
            // toggle, not a duplicate. Single-select polls additionally keep only
            // one row per (poll, user), enforced in the vote handler.
            entity.HasIndex(e => new { e.PollId, e.UserId, e.PollOptionId }).IsUnique();
            entity.HasIndex(e => e.PollId);                      // tally render
            entity.HasIndex(e => new { e.PollId, e.UserId });    // a member's votes / participation
        });

        // ── Support tickets ───────────────────────────────────────────
        // One row per ticket opened from the panel. Status + Priority are
        // stored as int (SQLite has no native enum) — same pattern as
        // OfficerApplication.Status. Indexes cover the expected reads:
        //   • ThreadId unique — button/close handlers map a thread back to its
        //     ticket row; a thread hosts exactly one ticket.
        //   • ControlMessageId — locate the embed to edit its state in place.
        //   • (GuildId, Status, LastActivityUtc) — the escalation/auto-close
        //     sweep's "open tickets, oldest activity first" scan.
        //   • (GuildId, OpenerUserId, Status) — "does this member already have
        //     an open ticket?" spam guard + their ticket history.
        //   • (Status, ReserveAssigned, LeaveEndUtc) — time-off Reserve-removal
        //     sweep ("approved leaves whose window has ended").
        modelBuilder.Entity<SupportTicket>(e =>
        {
            e.Property(t => t.Status).HasConversion<int>();
            e.Property(t => t.Priority).HasConversion<int>();

            e.HasIndex(t => t.ThreadId).IsUnique();
            e.HasIndex(t => t.ControlMessageId);
            e.HasIndex(t => new { t.GuildId, t.Status, t.LastActivityUtc });
            e.HasIndex(t => new { t.GuildId, t.OpenerUserId, t.Status });
            e.HasIndex(t => new { t.Status, t.ReserveAssigned, t.LeaveEndUtc });
            e.HasIndex(t => t.LeaveScheduled); // leave-management sweep

            // Atomic backstop for "one open anonymous ticket per user" — a
            // filtered unique index over only the open (Status != Closed=3)
            // anonymous rows. This closes the check-then-insert race where a
            // scripted client could submit two anonymous tickets at once; the
            // insert of the second throws and is caught in HandleCreateAsync.
            e.HasIndex(t => new { t.GuildId, t.OpenerUserId })
                .IsUnique()
                .HasFilter("\"IsAnonymous\" = 1 AND \"Status\" <> 3")
                .HasDatabaseName("IX_SupportTickets_OneOpenAnonPerUser");
        });

        modelBuilder.Entity<SupportTicketMessage>(e =>
        {
            e.Property(m => m.Direction).HasConversion<int>();
            // Transcript render: all messages for a ticket in send order.
            e.HasIndex(m => new { m.TicketId, m.SentUtc });
        });

        // ── Palworld sessions ─────────────────────────────────────────
        // Three read patterns, all hit on every 60s poll:
        //   • The poller's own "which sessions are still open?" query. Filtered
        //     to the open rows so the index stays tiny (a handful of rows) no
        //     matter how many thousands of closed sessions accumulate.
        //   • Per-player history — /palworld-playtime sums every session for one
        //     PalworldUserId, and the poller closes the newest open one on leave.
        //   • Name lookup, for the `name:` path of /palworld-playtime (which
        //     deliberately does NOT require a Discord link, so it works for
        //     people who never joined the server).
        modelBuilder.Entity<PalworldSession>(e =>
        {
            e.HasIndex(s => s.EndedUtc).HasFilter("\"EndedUtc\" IS NULL");
            e.HasIndex(s => new { s.PalworldUserId, s.StartedUtc });
            e.HasIndex(s => s.PlayerName);
        });

        // ── Palworld ↔ Discord links ──────────────────────────────────
        // Unique in BOTH directions: one Discord account per Palworld identity
        // and vice versa. The handler deletes the other side before inserting,
        // so these indexes are the structural backstop preventing two members
        // from both claiming the same character.
        modelBuilder.Entity<PalworldLink>(e =>
        {
            e.HasIndex(l => new { l.GuildId, l.DiscordUserId }).IsUnique();
            e.HasIndex(l => new { l.GuildId, l.PalworldUserId }).IsUnique();
        });

        // ── Palworld health samples ───────────────────────────────────
        // Every read is a time-window scan ("the last N hours"), and the retention
        // prune deletes by age, so a single index on SampledUtc covers both.
        modelBuilder.Entity<PalworldMetricSample>(e =>
        {
            e.HasIndex(s => s.SampledUtc);
        });
    }
}

/// <summary>
/// Allows EF Core tools (dotnet ef migrations, etc.) to create the DbContext
/// without running Program.cs.
/// </summary>
public class BotDbContextFactory : IDesignTimeDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BotDbContext>();
        optionsBuilder.UseSqlite("Data Source=clanguard.db");
        return new BotDbContext(optionsBuilder.Options);
    }
}