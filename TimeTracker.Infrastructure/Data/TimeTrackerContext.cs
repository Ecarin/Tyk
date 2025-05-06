using Microsoft.EntityFrameworkCore;
using TimeTracker.Domain.Entities;
using TimeTracker.Domain.Interfaces;

namespace TimeTracker.Infrastructure.Data;

public class TimeTrackerContext : DbContext, ITimeTrackerContext
{
    public TimeTrackerContext(DbContextOptions<TimeTrackerContext> options) : base(options) { }

    public DbSet<TimeEntry> TimeEntries { get; set; }
    public DbSet<ChatMessage> ChatMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TimeEntry>()
            .HasIndex(e => new { e.UserId, e.Timestamp });
    
        modelBuilder.Entity<TimeEntry>()
            .HasIndex(e => e.IsActive)
            .HasFilter("IsActive = 1");
        
        modelBuilder.Entity<ChatMessage>()
            .HasIndex(e => e.ChatId)
            .IsUnique();
    }
}