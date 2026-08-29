using OfficeCal.Core.Enums;

namespace OfficeCal.Core.Dtos;

/// <summary>
/// 使用者在畫面上看到的結構化重複設定。系統中除了 RruleFormatter 之外，
/// 沒有任何地方會直接處理 RRULE 字串。
/// </summary>
public class RecurrencePatternDto
{
    public RecurrenceFrequency Frequency { get; set; }

    /// <summary>每 N 天／週／月／年。</summary>
    public int Interval { get; set; } = 1;

    /// <summary>FREQ=WEEKLY 時的星期核取方塊，可複選。</summary>
    public List<DayOfWeek> ByWeekDays { get; set; } = new();

    /// <summary>FREQ=MONTHLY 時的兩種模式擇一。</summary>
    public MonthlyMode MonthlyMode { get; set; } = MonthlyMode.DayOfMonth;

    /// <summary>每月 N 日（1–31）；FREQ=YEARLY 時為每年 N 月的 N 日。</summary>
    public int? ByMonthDay { get; set; }

    /// <summary>每月第 N 個（1–4），-1 表示最後一個。</summary>
    public int? BySetPosition { get; set; }

    /// <summary>搭配 BySetPosition 的星期。</summary>
    public DayOfWeek? ByPositionWeekDay { get; set; }

    /// <summary>FREQ=YEARLY 的月份（1–12）。</summary>
    public int? ByMonth { get; set; }

    public RecurrenceEndMode EndMode { get; set; } = RecurrenceEndMode.UntilDate;
    public DateOnly? UntilDate { get; set; }
    public int? Count { get; set; }
}
