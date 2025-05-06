namespace Tyk.Domain.Entities;

public class ChatMessage
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public DateTime LastUpdated { get; set; }
}