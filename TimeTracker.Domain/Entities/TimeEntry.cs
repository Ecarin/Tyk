public class TimeEntry
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string? Username { get; set; } // Nullable
    public string? FirstName { get; set; } // Nullable
    public string? LastName { get; set; } // Nullable
    public long ChatId { get; set; }
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = null!;
    public bool IsActive { get; set; }
}