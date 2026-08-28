using OfficeCal.Core.Enums;

namespace OfficeCal.Core.Entities;

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class User
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Employee;
    public string IcsFeedToken { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class Room
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int Capacity { get; set; }
    public string? Equipment { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>事件本體 / 系列定義。StartAt、EndAt 為系列首次發生的時間。</summary>
public class Event
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int OwnerId { get; set; }
    public User? Owner { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    /// <summary>RRULE 字串；null 表示單次事件。唯一寫入者為 RruleFormatter。</summary>
    public string? RecurrenceRule { get; set; }
    public EventStatus Status { get; set; } = EventStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<EventOccurrence> Occurrences { get; set; } = new();
    public List<EventAttendee> Attendees { get; set; } = new();
}

/// <summary>唯一的權威占用表。單次事件也一律產生 1 筆。</summary>
public class EventOccurrence
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event? Event { get; set; }
    /// <summary>展開時的原始起始時間，等同 iCalendar 的 RECURRENCE-ID。</summary>
    public DateTime OriginalStartAt { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public int? RoomId { get; set; }
    public Room? Room { get; set; }
    public string? TitleOverride { get; set; }
    public bool IsModified { get; set; }
    public bool IsCancelled { get; set; }
}

public class EventAttendee
{
    public int EventId { get; set; }
    public Event? Event { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public NotificationType Type { get; set; }
    public int? EventId { get; set; }
    /// <summary>產生當下就寫成完整句子。</summary>
    public string Message { get; set; } = "";
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}
