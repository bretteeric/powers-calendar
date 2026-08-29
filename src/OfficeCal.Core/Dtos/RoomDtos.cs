using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

public class RoomDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int Capacity { get; set; }
    public string? Equipment { get; set; }
    public bool IsActive { get; set; }
}

public class RoomRequest
{
    [Required(ErrorMessage = "請輸入會議廳名稱")]
    [StringLength(50)] public string Name { get; set; } = "";
    [StringLength(100)] public string? Location { get; set; }
    [Range(1, 1000, ErrorMessage = "容納人數必須介於 1 到 1000")] public int Capacity { get; set; }
    [StringLength(200)] public string? Equipment { get; set; }
    public bool IsActive { get; set; } = true;
}

public class BusySlotDto
{
    public int OccurrenceId { get; set; }
    public int EventId { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string Title { get; set; } = "";
    public string OwnerName { get; set; } = "";
}

/// <summary>資源時間軸頁的一列：一間會議廳與它當日的占用時段。</summary>
public class RoomAvailabilityDto
{
    public int RoomId { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public int Capacity { get; set; }
    public string? Equipment { get; set; }
    public List<BusySlotDto> Busy { get; set; } = new();
}
