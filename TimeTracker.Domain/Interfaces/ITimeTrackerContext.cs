using Microsoft.EntityFrameworkCore;
using TimeTracker.Domain.Entities;

namespace TimeTracker.Domain.Interfaces;

public interface ITimeTrackerContext
{
    DbSet<TimeEntry> TimeEntries { get; set; }
    DbSet<ChatMessage> ChatMessages { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}