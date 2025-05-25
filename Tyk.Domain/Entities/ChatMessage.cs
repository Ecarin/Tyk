using System.ComponentModel.DataAnnotations;

namespace Tyk.Domain.Entities;

public class ChatMessage
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int MessageId { get; set; }
    public DateTime LastUpdated { get; set; }
    [StringLength(30)] public string MessageType { get; set; } = "status"; // "status" | "welcome"
}