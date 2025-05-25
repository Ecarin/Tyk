using Microsoft.EntityFrameworkCore;
using Tyk.Application.Interfaces;
using Tyk.Domain.Entities;
using Tyk.Domain.Interfaces;

namespace Tyk.Infrastructure.Repositories;

public class TimeEntryRepository(ITimeTrackerContext _context) : ITimeEntryRepository
{
    private const string STATUS = "status";
    private const string WELCOME = "welcome";

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
    {
        return await _context.TimeEntries
            .Where(e => e.ChatId == chatId && e.Timestamp >= from && e.Timestamp <= to)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();
    }

    public async Task<ChatMessage?> GetChatMessageAsync(long chatId)
    {
        return await _context.ChatMessages.FirstOrDefaultAsync(c => c.ChatId == chatId && c.MessageType == STATUS);
    }

    public async Task SaveChatMessageAsync(ChatMessage message)
    {
        var existing = await _context.ChatMessages
            .FirstOrDefaultAsync(c => c.ChatId == message.ChatId && c.MessageType == message.MessageType);

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
        var message = await _context.ChatMessages
            .FirstOrDefaultAsync(c => c.ChatId == chatId && c.MessageType == STATUS);
        if (message != null)
        {
            _context.ChatMessages.Remove(message);
            await _context.SaveChangesAsync();
        }
    }

    public async Task<List<long>> GetTrackedChatsAsync()
    {
        return await _context.ChatMessages.Select(c => c.ChatId).ToListAsync();
    }

    public async Task<ChatMessage?> GetWelcomeMessageAsync(long chatId)
    {
        return await _context.ChatMessages.FirstOrDefaultAsync(c => c.ChatId == chatId && c.MessageType == WELCOME);
    }

    public async Task SaveWelcomeMessageAsync(ChatMessage msg)
    {
        msg.MessageType = WELCOME;
        var existing = await GetWelcomeMessageAsync(msg.ChatId);

        if (existing != null)
        {
            existing.MessageId = msg.MessageId;
            existing.LastUpdated = msg.LastUpdated;
            _context.ChatMessages.Update(existing);
        }
        else
        {
            await _context.ChatMessages.AddAsync(msg);
        }

        await _context.SaveChangesAsync();
    }

    public async Task<DateTime?> GetOldestEntryDateAsync()
    {
        return await _context.TimeEntries
            .OrderBy(e => e.Timestamp)
            .Select(e => (DateTime?)e.Timestamp)
            .FirstOrDefaultAsync();
    }
}