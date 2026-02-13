using ClanGuardBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClanGuardBot.Data;

public class BotDbContext : DbContext
{
    public DbSet<UserActivity> UserActivities => Set<UserActivity>();
    public DbSet<AwolRecord> AwolRecords => Set<AwolRecord>();

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
    }
}
