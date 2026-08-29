using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;

namespace OfficeCal.Services;

/// <summary>
/// 事件的業務規則與交易邊界。這是全系統唯一開啟／提交交易的地方（D2）。
/// </summary>
public interface IEventService
{
    Task<int> CreateAsync(CreateEventRequest req, CancellationToken ct = default);

    /// <summary>行事曆區間查詢。scope=Room 未附 roomId 時丟 ValidationException。</summary>
    Task<List<OccurrenceDto>> GetRangeAsync(DateTime from, DateTime to, CalendarScope scope,
                                            int? roomId, CancellationToken ct = default);

    Task<EventDetailDto> GetDetailAsync(int eventId, CancellationToken ct = default);

    Task UpdateAsync(int eventId, EditMode mode, UpdateEventRequest req, CancellationToken ct = default);

    Task CancelAsync(int eventId, EditMode mode, int? occurrenceId, CancellationToken ct = default);

    Task<List<AttendeeConflictDto>> CheckAttendeesAsync(AttendeeConflictRequest req,
                                                        CancellationToken ct = default);
}
