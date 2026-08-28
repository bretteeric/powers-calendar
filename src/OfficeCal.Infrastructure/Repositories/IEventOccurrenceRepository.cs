using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IEventOccurrenceRepository
{
    /// <summary>
    /// 取得某會議廳在 [from, to) 區間內、未取消的 occurrence，供衝突偵測比對。
    /// excludeEventId 不為 null 時排除該事件自己的 occurrence（編輯既有事件時使用）。
    /// 已 Include Event、Event.Owner、Room，供組裝 409 明細。
    /// </summary>
    Task<List<EventOccurrence>> GetRoomOccurrencesAsync(
        int roomId, DateTime from, DateTime to, int? excludeEventId, CancellationToken ct = default);

    /// <summary>scope=me：使用者擁有或被邀請的 occurrence。</summary>
    Task<List<EventOccurrence>> GetRangeForUserAsync(
        int userId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>scope=room：指定會議廳的所有 occurrence，不分擁有者。</summary>
    Task<List<EventOccurrence>> GetRangeForRoomAsync(
        int roomId, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>scope=all：所有已掛會議廳的 occurrence（不含他人的純個人事件）。</summary>
    Task<List<EventOccurrence>> GetRangeAllRoomsAsync(
        DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>與會者行程衝突警示用：這批使用者擁有或被邀請的 occurrence。</summary>
    Task<List<EventOccurrence>> GetRangeForUsersAsync(
        IReadOnlyCollection<int> userIds, DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>單筆編輯用，回傳受追蹤的實體（要寫回）。</summary>
    Task<EventOccurrence?> GetTrackedByIdAsync(int occurrenceId, CancellationToken ct = default);

    /// <summary>系列重新展開用，回傳受追蹤的整串 occurrence。</summary>
    Task<List<EventOccurrence>> GetTrackedByEventAsync(int eventId, CancellationToken ct = default);
}
