using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IRoomRepository
{
    Task<Room?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<Room>> ListAsync(bool activeOnly, CancellationToken ct = default);

    /// <summary>
    /// 於呼叫端已開啟的交易內，對 Rooms 中該列下 UPDLOCK, HOLDLOCK 並回傳它。
    /// 這是全系統防止雙重預約的關鍵：所有寫入 EventOccurrence 的路徑都必須先經過這裡。
    /// 會議廳不存在時回傳 null。
    /// </summary>
    Task<Room?> LockAndGetAsync(int roomId, CancellationToken ct = default);

    void Add(Room room);
}
