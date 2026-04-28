using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClanGuardBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<UserActivity>       UserActivities      => Set<UserActivity>();
    public DbSet<AwolRecord>         AwolRecords         => Set<AwolRecord>();
    public DbSet<MessageEvent>       MessageEvents       => Set<MessageEvent>();
    public DbSet<VoiceSession>       VoiceSessions       => Set<VoiceSession>();
    public DbSet<RankHistory>        RankHistories       => Set<RankHistory>();
    public DbSet<TicketReminder>     TicketReminders     => Set<TicketReminder>();
    public DbSet<GuestReminder>      GuestReminders      => Set<GuestReminder>();
    public DbSet<OnboardingReminder> OnboardingReminders => Set<OnboardingReminder>();
    public DbSet<CalendarEvent>      CalendarEvents      => Set<CalendarEvent>();
    public DbSet<EventAttendance>    EventAttendances    => Set<EventAttendance>();
    public DbSet<BotState>           BotStates           => Set<BotState>();
    public DbSet<BumpState>          BumpStates          => Set<BumpState>();

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
            // Look up by Discord message ID to detect duplicates and handle edits/deletes
            e.HasIndex(c => c.DiscordMessageId);
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