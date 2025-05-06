using Microsoft.EntityFrameworkCore;
using Tyk.Domain.Entities;
using Tyk.Domain.Interfaces;
using Tyk.Application.Interfaces;

namespace Tyk.Infrastructure.Repositories;

public class TimeEntryRepository(ITimeTrackerContext _context) : ITimeEntryRepository
{
    public async Task AddEntryAsync(TimeEntry entry)
    {
        // Check if user has existing active "in" status
        if (entry.Action == "in")
        {
            var activeEntry = await _context.TimeEntries
                .Where(e => e.UserId == entry.UserId && e.IsActive)
                .FirstOrDefaultAsync();

            if (activeEntry != null)
            {
                activeEntry.IsActive = false;
                _context.TimeEntries.Update(activeEntry);
            }
        }

        entry.IsActive = entry.Action == "in";
        await _context.TimeEntries.AddAsync(entry);
        await _context.SaveChangesAsync();
    }

    public async Task<List<TimeEntry>> GetUserEntriesAsync(long chatId, DateTime from, DateTime to)
        => await _context.TimeEntries
            .Where(e => e.ChatId == chatId && e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

    public async Task<ChatMessage?> GetChatMessageAsync(long chatId)
        => await _context.ChatMessages.FirstOrDefaultAsync(c => c.ChatId == chatId);

    public async Task SaveChatMessageAsync(ChatMessage message)
    {
        var existing = await GetChatMessageAsync(message.ChatId);
        if (existing != null)
        {
            existing.MessageId = message.MessageId;
            existing.LastUpdated = message.LastUpdated;
            _context.ChatMessages.Update(existing);
        }
        else
        {
            await _context.ChatMessages.AddAsync(message);
        }

        await _context.SaveChangesAsync();
    }

    public async Task DeleteChatMessageAsync(long chatId)
    {
        var message = await GetChatMessageAsync(chatId);
        if (message != null)
        {
            _context.ChatMessages.Remove(message);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<long>> GetTrackedChatsAsync()
        => await _context.ChatMessages.Select(c => c.ChatId).ToListAsync();
}