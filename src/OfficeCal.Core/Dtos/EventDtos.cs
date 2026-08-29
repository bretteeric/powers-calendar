using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

public class CreateEventRequest
{
    [Required(ErrorMessage = "請輸入標題")]
    [StringLength(100, ErrorMessage = "標題最多 100 字")]
    public string Title { get; set; } = "";

    [StringLength(1000, ErrorMessage = "說明最多 1000 字")]
    public string? Description { get; set; }

    /// <summary>null 表示純個人事件，不占用資源。</summary>
    public int? RoomId { get; set; }

    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    public List<int> AttendeeIds { get; set; } = new();

    /// <summary>null 表示單次事件。</summary>
    public RecurrencePatternDto? Recurrence { get; set; }
}

public class UpdateEventRequest : CreateEventRequest
{
    /// <summary>mode=single 時必填。</summary>
    public int? OccurrenceId { get; set; }
}

/// <summary>行事曆格子上的一筆。所有檢視都只讀 occurrence。</summary>
public class OccurrenceDto
{
    public int OccurrenceId { get; set; }
    public int EventId { get; set; }
    public string Title { get; set; } = "";
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    public int? RoomId { get; set; }
    public string? RoomName { get; set; }
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public bool IsRecurring { get; set; }
    public bool IsModified { get; set; }
    public bool CanEdit { get; set; }
}

public class AttendeeDto
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? DepartmentName { get; set; }
}

public class EventDetailDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int? RoomId { get; set; }
    public string? RoomName { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public bool IsAllDay { get; set; }
    public string Status { get; set; } = "";
    public int OwnerId { get; set; }
    public string OwnerName { get; set; } = "";
    public RecurrencePatternDto? Recurrence { get; set; }
    public List<AttendeeDto> Attendees { get; set; } = new();
    public bool CanEdit { get; set; }
}

public class TimeSlotDto
{
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
}

public class AttendeeConflictRequest
{
    public List<int> AttendeeIds { get; set; } = new();
    public List<TimeSlotDto> Slots { get; set; } = new();
}

public class AttendeeConflictDto
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public int ConflictCount { get; set; }
    public List<string> Titles { get; set; } = new();
}
