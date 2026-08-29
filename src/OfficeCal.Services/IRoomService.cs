using OfficeCal.Core.Dtos;

namespace OfficeCal.Services;

public interface IRoomService
{
    Task<List<RoomDto>> ListAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>指定日期各會議廳的占用時段，可依最低容納人數過濾。</summary>
    Task<List<RoomAvailabilityDto>> GetAvailabilityAsync(DateOnly date, int? minCapacity,
                                                          CancellationToken ct = default);

    Task<int> CreateAsync(RoomRequest req, CancellationToken ct = default);
    Task UpdateAsync(int id, RoomRequest req, CancellationToken ct = default);
}
