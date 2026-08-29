using System.Globalization;
using System.Text;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;

namespace OfficeCal.Services;

/// <summary>
/// 結構化重複設定 ↔ RRULE 字串的純函式轉換。
/// 這是資料庫 Event.RecurrenceRule 欄位的唯一寫入者，所以 Parse 只需認得 ToRrule 寫得出來的子集。
/// </summary>
public static class RruleFormatter
{
    public const int MaxOccurrences = 730;

    private static readonly string[] DayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    private static string Code(DayOfWeek d) => DayCodes[(int)d];

    private static DayOfWeek Day(string code)
    {
        var i = Array.IndexOf(DayCodes, code.ToUpperInvariant());
        if (i < 0) throw new ValidationException($"無法辨識的星期代碼 '{code}'");
        return (DayOfWeek)i;
    }

    public static string ToRrule(RecurrencePatternDto p)
    {
        Validate(p);

        var sb = new StringBuilder();
        sb.Append("FREQ=").Append(p.Frequency.ToString().ToUpperInvariant());
        sb.Append(";INTERVAL=").Append(p.Interval);

        if (p.Frequency == RecurrenceFrequency.Yearly)
            sb.Append(";BYMONTH=").Append(p.ByMonth!.Value);

        if (p.Frequency == RecurrenceFrequency.Yearly ||
            (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.DayOfMonth))
            sb.Append(";BYMONTHDAY=").Append(p.ByMonthDay!.Value);

        if (p.Frequency == RecurrenceFrequency.Weekly)
            sb.Append(";BYDAY=").Append(string.Join(",", p.ByWeekDays.OrderBy(d => (int)d).Select(Code)));

        if (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.WeekDayOfMonth)
        {
            sb.Append(";BYDAY=").Append(Code(p.ByPositionWeekDay!.Value));
            sb.Append(";BYSETPOS=").Append(p.BySetPosition!.Value);
        }

        if (p.EndMode == RecurrenceEndMode.UntilDate)
            sb.Append(";UNTIL=").Append(p.UntilDate!.Value.ToString("yyyyMMdd")).Append("T235959");
        else
            sb.Append(";COUNT=").Append(p.Count!.Value);

        return sb.ToString();
    }

    public static RecurrencePatternDto Parse(string rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule)) throw new ValidationException("重複規則為空字串");

        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.Split('=', 2))
                         .ToDictionary(
                             x => x[0].Trim().ToUpperInvariant(),
                             x => x.Length > 1 ? x[1].Trim() : "");

        if (!parts.TryGetValue("FREQ", out var freq))
            throw new ValidationException("重複規則缺少 FREQ");

        var p = new RecurrencePatternDto
        {
            Frequency = freq.ToUpperInvariant() switch
            {
                "DAILY" => RecurrenceFrequency.Daily,
                "WEEKLY" => RecurrenceFrequency.Weekly,
                "MONTHLY" => RecurrenceFrequency.Monthly,
                "YEARLY" => RecurrenceFrequency.Yearly,
                _ => throw new ValidationException($"不支援的 FREQ '{freq}'"),
            },
            Interval = parts.TryGetValue("INTERVAL", out var iv)
                ? int.Parse(iv, CultureInfo.InvariantCulture) : 1,
        };

        if (parts.TryGetValue("BYMONTH", out var bm))
            p.ByMonth = int.Parse(bm, CultureInfo.InvariantCulture);

        if (parts.TryGetValue("BYMONTHDAY", out var bmd))
            p.ByMonthDay = int.Parse(bmd, CultureInfo.InvariantCulture);

        var byDays = parts.TryGetValue("BYDAY", out var bd) && bd.Length > 0
            ? bd.Split(',').Select(Day).ToList()
            : new List<DayOfWeek>();

        if (parts.TryGetValue("BYSETPOS", out var bsp))
        {
            p.MonthlyMode = MonthlyMode.WeekDayOfMonth;
            p.BySetPosition = int.Parse(bsp, CultureInfo.InvariantCulture);
            p.ByPositionWeekDay = byDays.Count > 0
                ? byDays[0]
                : throw new ValidationException("BYSETPOS 必須搭配 BYDAY");
        }
        else if (p.Frequency == RecurrenceFrequency.Weekly)
        {
            p.ByWeekDays = byDays;
        }

        if (parts.TryGetValue("UNTIL", out var until))
        {
            p.EndMode = RecurrenceEndMode.UntilDate;
            p.UntilDate = DateOnly.ParseExact(until[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
        }
        else if (parts.TryGetValue("COUNT", out var cnt))
        {
            p.EndMode = RecurrenceEndMode.Count;
            p.Count = int.Parse(cnt, CultureInfo.InvariantCulture);
        }
        else
        {
            throw new ValidationException("重複規則必須有結束條件（UNTIL 或 COUNT）");
        }

        Validate(p);
        return p;
    }

    private static void Validate(RecurrencePatternDto p)
    {
        if (p.Interval < 1 || p.Interval > 999)
            throw new ValidationException("重複間隔必須介於 1 到 999 之間");

        switch (p.EndMode)
        {
            case RecurrenceEndMode.UntilDate when p.UntilDate is null:
                throw new ValidationException("重複事件必須指定結束日期或重複次數");
            case RecurrenceEndMode.Count when p.Count is null:
                throw new ValidationException("重複事件必須指定結束日期或重複次數");
            case RecurrenceEndMode.Count when p.Count < 1:
                throw new ValidationException("重複次數必須至少為 1");
            case RecurrenceEndMode.Count when p.Count > MaxOccurrences:
                throw new ValidationException($"重複次數超過上限（{MaxOccurrences} 次），請縮短結束日期");
        }

        switch (p.Frequency)
        {
            case RecurrenceFrequency.Weekly when p.ByWeekDays.Count == 0:
                throw new ValidationException("每週重複必須至少勾選一個星期");
            case RecurrenceFrequency.Weekly when p.ByWeekDays.Distinct().Count() != p.ByWeekDays.Count:
                throw new ValidationException("星期不可重複勾選");

            case RecurrenceFrequency.Monthly when p.MonthlyMode == MonthlyMode.DayOfMonth
                                               && p.ByMonthDay is not (>= 1 and <= 31):
                throw new ValidationException("每月 N 日必須介於 1 到 31 之間");
            case RecurrenceFrequency.Monthly when p.MonthlyMode == MonthlyMode.WeekDayOfMonth
                                               && (p.ByPositionWeekDay is null
                                                   || p.BySetPosition is not (1 or 2 or 3 or 4 or -1)):
                throw new ValidationException("每月第 N 個星期 X 的設定不完整");

            case RecurrenceFrequency.Yearly when p.ByMonth is not (>= 1 and <= 12):
                throw new ValidationException("每年重複的月份必須介於 1 到 12 之間");
            case RecurrenceFrequency.Yearly when p.ByMonthDay is not (>= 1 and <= 31):
                throw new ValidationException("每年重複的日期必須介於 1 到 31 之間");
        }
    }
}
