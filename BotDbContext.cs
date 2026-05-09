using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClanGuardBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<UserActivity>        UserActivities      => Set<UserActivity>();
    public DbSet<AwolRecord>          AwolRecords         => Set<AwolRecord>();
    public DbSet<AwolKickAuditRecord> AwolKickAudits      => Set<AwolKickAuditRecord>();
    public DbSet<MessageEvent>        MessageEvents       => Set<MessageEvent>();
    public DbSet<VoiceSession>        VoiceSessions       => Set<VoiceSession>();
    public DbSet<RankHistory>         RankHistories       => Set<RankHistory>();
    public DbSet<TicketReminder>      TicketReminders     => Set<TicketReminder>();
    public DbSet<GuestReminder>       GuestReminders      => Set<GuestReminder>();
    public DbSet<OnboardingReminder>  OnboardingReminders => Set<OnboardingReminder>();
    public DbSet<CalendarEvent>       CalendarEvents      => Set<CalendarEvent>();
    public DbSet<EventAttendance>     EventAttendances    => Set<EventAttendance>();
    public DbSet<BotState>            BotStates           => Set<BotState>();
    public DbSet<BumpState>           BumpStates          => Set<BumpState>();
    public DbSet<ApolloMessageLog>    ApolloMessageLogs   => Set<ApolloMessageLog>();
    public DbSet<ApolloEvent>         ApolloEvents        => Set<ApolloEvent>();
    public DbSet<InviteSource>        InviteSources       => Set<InviteSource>();
    public DbSet<InviteJoin>          InviteJoins         => Set<InviteJoin>();
    public DbSet<RedditLead>          RedditLeads         => Set<RedditLead>();

    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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