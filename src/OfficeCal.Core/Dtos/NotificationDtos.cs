namespace OfficeCal.Core.Dtos;

public class NotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public int? EventId { get; set; }
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class NotificationListDto
{
    public List<NotificationDto> Items { get; set; } = new();
    public int UnreadCount { get; set; }
}
