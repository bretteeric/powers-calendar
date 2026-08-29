using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

/// <summary>
/// 唯一能寫入 EventOccurrence 的地方（規格 6.1）。
/// 所有方法都必須在呼叫端已開啟的交易內執行——本服務自己不開交易、不提交。
/// </summary>
public interface IBookingService
{
    /// <summary>鎖定目標會議廳、檢查衝突、寫入全新的 occurrence。ev.Id 必須已存在。</summary>
    Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots, CancellationToken ct = default);

    /// <summary>
    /// 系列重新展開。保留「已發生過的」「被單獨修改的」「被單獨取消的」occurrence，
    /// 刪除其餘未來的，再依 slots 產生新的（跳過已保留的 OriginalStartAt）。
    /// 系列若換了會議廳，保留下來的未來 occurrence 一併搬到新會議廳並參與衝突檢查。
    /// </summary>
    Task ReExpandSeriesAsync(Event ev, IReadOnlyList<TimeSlot> slots, CancellationToken ct = default);

    /// <summary>
    /// 單筆改期（mode=single）。同樣要鎖會議廳並檢查衝突——
    /// 把一次發生移到別的時段一樣可能撞上既有預約。
    /// </summary>
    Task MoveOccurrenceAsync(EventOccurrence occ, DateTime newStart, DateTime newEnd,
                             CancellationToken ct = default);

    /// <summary>單筆僅改標題。不涉及時段，不取鎖、不檢查衝突、不需要交易。</summary>
    Task SetOccurrenceTitleAsync(EventOccurrence occ, string? title, CancellationToken ct = default);

    /// <summary>取消單一次發生。釋出時段不可能造成雙重預約，因此不需取鎖。</summary>
    Task CancelOccurrenceAsync(EventOccurrence occ, CancellationToken ct = default);

    /// <summary>取消整個系列：Event.Status = Cancelled，所有 occurrence 設 IsCancelled。</summary>
    Task CancelSeriesAsync(Event ev, CancellationToken ct = default);
}
