using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

/// <summary>
/// 站內通知。寫入方法都在呼叫端（EventService）的交易內執行，
/// 與 Event、EventOccurrence 的寫入是同一個原子操作。
/// 訊息在產生當下就寫成完整句子（規格 5.5）。
/// </summary>
public interface INotificationService
{
    /// <summary>建立事件並指定與會者。userIds 中若含擁有者會自動略過。</summary>
    Task AddedToEventAsync(Event ev, DateTime firstStart, IReadOnlyCollection<int> userIds,
                           string ownerName, CancellationToken ct = default);

    /// <summary>
    /// 編輯事件的時間或會議廳。
    /// occurrenceOriginalStart 非 null 表示 mode=single，訊息會標明是哪一次發生。
    /// roomName 非 null 表示會議廳有變更。
    /// </summary>
    Task EventUpdatedAsync(Event ev, IReadOnlyCollection<int> userIds,
                           DateTime? occurrenceOriginalStart, DateTime newStart, string? roomName,
                           CancellationToken ct = default);

    /// <summary>取消事件。occurrenceStart 非 null 表示 mode=single。</summary>
    Task EventCancelledAsync(Event ev, IReadOnlyCollection<int> userIds, DateTime? occurrenceStart,
                             CancellationToken ct = default);

    /// <summary>管理員強制取消他人預約。</summary>
    Task ForcedCancellationAsync(Event ev, IReadOnlyCollection<int> userIds, DateTime? occurrenceStart,
                                 string adminName, CancellationToken ct = default);

    Task<List<NotificationDto>> ListAsync(int userId, bool unreadOnly, int take,
                                          CancellationToken ct = default);
    Task<int> UnreadCountAsync(int userId, CancellationToken ct = default);

    /// <summary>只有收件者本人可標記已讀，否則丟 ForbiddenException。</summary>
    Task MarkReadAsync(int notificationId, int userId, CancellationToken ct = default);
}
