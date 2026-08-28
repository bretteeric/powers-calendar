using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;

namespace OfficeCal.Services;

/// <summary>
/// 全系統唯一 using Ical.Net 的服務。RRULE 字串 ↔ DTO 的轉換交給 RruleFormatter，
/// 這裡只負責把程式化建構的 RecurrencePattern 展開成發生時間清單。
/// </summary>
public class RecurrenceService : IRecurrenceService
{
    public const int MaxOccurrences = RruleFormatter.MaxOccurrences;   // 730

    public string ToRrule(RecurrencePatternDto pattern) => RruleFormatter.ToRrule(pattern);

    public RecurrencePatternDto ParseRrule(string rrule) => RruleFormatter.Parse(rrule);

    public IReadOnlyList<TimeSlot> Expand(string? rrule, DateTime startAt, DateTime endAt)
    {
        var duration = endAt - startAt;
        if (duration <= TimeSpan.Zero)
            throw new ValidationException("結束時間必須晚於開始時間");

        if (string.IsNullOrWhiteSpace(rrule))
            return new[]
            {
                new TimeSlot(DateTime.SpecifyKind(startAt, DateTimeKind.Unspecified),
                             DateTime.SpecifyKind(endAt, DateTimeKind.Unspecified)),
            };

        var pattern = RruleFormatter.Parse(rrule);   // 同時驗證結束條件必填
        ValidateStartMatches(pattern, startAt);

        var ev = new CalendarEvent
        {
            DtStart = ToCal(startAt),
            DtEnd = ToCal(endAt),
            RecurrenceRule = ToIcal(pattern),
        };

        // 規則一定有 UNTIL 或 COUNT，所以列舉必然終止；Take 只是防呆上限。
        var starts = ev.GetOccurrences()
                       .Take(MaxOccurrences + 1)
                       .Select(o => o.Period.StartTime.Value)
                       .ToList();

        if (starts.Count > MaxOccurrences)
            throw new ValidationException($"重複次數超過上限（{MaxOccurrences} 次），請縮短結束日期");

        if (starts.Count == 0)
            throw new ValidationException("此重複規則不會產生任何發生時間，請檢查設定");

        return starts
            .Select(s => new TimeSlot(DateTime.SpecifyKind(s, DateTimeKind.Unspecified),
                                      DateTime.SpecifyKind(s, DateTimeKind.Unspecified) + duration))
            .ToList();
    }

    public void ValidateStartMatches(RecurrencePatternDto p, DateTime startAt)
    {
        const string message = "重複規則與事件起始日不一致，請調整起始日或重複設定";

        switch (p.Frequency)
        {
            case RecurrenceFrequency.Daily:
                return;

            case RecurrenceFrequency.Weekly:
                if (!p.ByWeekDays.Contains(startAt.DayOfWeek)) throw new ValidationException(message);
                return;

            case RecurrenceFrequency.Monthly when p.MonthlyMode == MonthlyMode.DayOfMonth:
                if (p.ByMonthDay != startAt.Day) throw new ValidationException(message);
                return;

            case RecurrenceFrequency.Monthly:
                if (p.ByPositionWeekDay != startAt.DayOfWeek) throw new ValidationException(message);
                if (p.BySetPosition == -1)
                {
                    var daysInMonth = DateTime.DaysInMonth(startAt.Year, startAt.Month);
                    if (startAt.Day + 7 <= daysInMonth) throw new ValidationException(message);
                }
                else if (p.BySetPosition != (startAt.Day - 1) / 7 + 1)
                {
                    throw new ValidationException(message);
                }
                return;

            case RecurrenceFrequency.Yearly:
                if (p.ByMonth != startAt.Month || p.ByMonthDay != startAt.Day)
                    throw new ValidationException(message);
                return;
        }
    }

    /// <summary>不帶時區的 floating time。台灣無日光節約，浮動時間即台北當地時間。</summary>
    private static CalDateTime ToCal(DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);

    private static RecurrencePattern ToIcal(RecurrencePatternDto p)
    {
        var r = new RecurrencePattern
        {
            Frequency = p.Frequency switch
            {
                RecurrenceFrequency.Daily => FrequencyType.Daily,
                RecurrenceFrequency.Weekly => FrequencyType.Weekly,
                RecurrenceFrequency.Monthly => FrequencyType.Monthly,
                _ => FrequencyType.Yearly,
            },
            Interval = p.Interval,
        };

        if (p.Frequency == RecurrenceFrequency.Weekly)
            r.ByDay = p.ByWeekDays.Select(d => new WeekDay(d)).ToList();

        if (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.WeekDayOfMonth)
        {
            r.ByDay = new List<WeekDay> { new(p.ByPositionWeekDay!.Value) };
            r.BySetPosition = new List<int> { p.BySetPosition!.Value };
        }

        if (p.Frequency == RecurrenceFrequency.Monthly && p.MonthlyMode == MonthlyMode.DayOfMonth)
            r.ByMonthDay = new List<int> { p.ByMonthDay!.Value };

        if (p.Frequency == RecurrenceFrequency.Yearly)
        {
            r.ByMonth = new List<int> { p.ByMonth!.Value };
            r.ByMonthDay = new List<int> { p.ByMonthDay!.Value };
        }

        if (p.EndMode == RecurrenceEndMode.Count)
            r.Count = p.Count!.Value;
        else
            r.Until = new CalDateTime(p.UntilDate!.Value.Year, p.UntilDate.Value.Month,
                                      p.UntilDate.Value.Day, 23, 59, 59);

        return r;
    }
}
