using Microsoft.EntityFrameworkCore;
using Tyk.Domain.Entities;

namespace Tyk.Domain.Interfaces;

public interface ITimeTrackerContext
{
    DbSet<TimeEntry> TimeEntries { get; set; }
    DbSet<ChatMessage> ChatMessages { get; set; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}