using OfficeCal.Core.Dtos;

namespace OfficeCal.Services;

/// <summary>
/// 唯一 using Ical.Net 的服務。其他層只認識 RecurrencePatternDto 與 TimeSlot。
/// </summary>
public interface IRecurrenceService
{
    /// <summary>結構化設定 → RRULE 字串（含驗證）。</summary>
    string ToRrule(RecurrencePatternDto pattern);

    /// <summary>RRULE 字串 → 結構化設定（含驗證）。</summary>
    RecurrencePatternDto ParseRrule(string rrule);

    /// <summary>
    /// 展開重複規則。rrule 為 null 時回傳單一 TimeSlot。
    /// 每次發生的長度一律等於 (endAt - startAt)。
    /// 展開超過 730 筆或規則無結束條件時丟 ValidationException。
    /// </summary>
    IReadOnlyList<TimeSlot> Expand(string? rrule, DateTime startAt, DateTime endAt);

    /// <summary>
    /// 驗證事件起始日符合重複規則。不符時丟 ValidationException。
    /// 前端的重複設定器預設會以起始日填入星期／日期，所以正常操作不會踩到。
    /// </summary>
    void ValidateStartMatches(RecurrencePatternDto pattern, DateTime startAt);
}
