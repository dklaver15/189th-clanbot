using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClanGuardBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<UserActivity> UserActivities => Set<UserActivity>();
    public DbSet<AwolRecord> AwolRecords => Set<AwolRecord>();
    public DbSet<MessageEvent> MessageEvents => Set<MessageEvent>();
    public DbSet<VoiceSession> VoiceSessions => Set<VoiceSession>();
    public DbSet<RankHistory> RankHistories => Set<RankHistory>();
    public DbSet<TicketReminder> TicketReminders => Set<TicketReminder>();

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