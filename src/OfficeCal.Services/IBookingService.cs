using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

/// <summary>
/// 唯一能寫入 EventOccurrence 的地方（規格 6.1）。
/// 所有方法都必須在呼叫端已開啟的交易內執行——本服務自己不開交易、不提交。
/// 任務 7 會再加入 ReExpandSeriesAsync 與 MoveOccurrenceAsync。
/// </summary>
public interface IBookingService
{
    /// <summary>
    /// 鎖定目標會議廳、檢查衝突、寫入全新的 occurrence。
    /// ev.Id 必須已存在（呼叫端先 SaveChanges 取得）。
    /// </summary>
    Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots, CancellationToken ct = default);

    /// <summary>取消單一次發生。釋出時段不可能造成雙重預約，因此不需取鎖。</summary>
    Task CancelOccurrenceAsync(EventOccurrence occ, CancellationToken ct = default);

    /// <summary>取消整個系列：Event.Status = Cancelled，所有 occurrence 設 IsCancelled。</summary>
    Task CancelSeriesAsync(Event ev, CancellationToken ct = default);
}
