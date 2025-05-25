using Tyk.Domain.Entities;

namespace Tyk.Application.Interfaces;

public interface ITimeEntryRepository
{
    Task AddEntryAsync(TimeEntry entry);
    Task<List<TimeEntry>> GetUserEntriesAsync(long userId, DateTime from, DateTime to);
    Task<ChatMessage?> GetChatMessageAsync(long chatId);
    Task SaveChatMessageAsync(ChatMessage message);
    Task DeleteChatMessageAsync(long chatId);
    Task<List<long>> GetTrackedChatsAsync();
    Task<ChatMessage?> GetWelcomeMessageAsync(long chatId);
    Task SaveWelcomeMessageAsync(ChatMessage msg);
    Task<DateTime?> GetOldestEntryDateAsync();
}