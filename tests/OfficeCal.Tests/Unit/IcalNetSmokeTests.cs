using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Xunit;

namespace OfficeCal.Tests.Unit;

/// <summary>
/// 固定住本專案實際用到的那一小塊 Ical.Net API。
/// 升級套件時若這支測試壞掉，就知道要調整 RecurrenceService。
/// </summary>
public class IcalNetSmokeTests
{
    [Fact]
    public void 每週一展開三次()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = FrequencyType.Weekly,
            Interval = 1,
            ByDay = new List<WeekDay> { new(DayOfWeek.Monday) },
            Count = 3,
        };

        var ev = new CalendarEvent
        {
            // 刻意使用不帶時區的 floating time：台灣無日光節約，浮動時間即台北當地時間。
            DtStart = new CalDateTime(2026, 9, 7, 10, 0, 0),
            DtEnd = new CalDateTime(2026, 9, 7, 11, 0, 0),
            RecurrenceRule = pattern,
        };

        var starts = ev.GetOccurrences()
                       .Take(10)
                       .Select(o => o.Period.StartTime.Value)   // 若編譯失敗見下方註記
                       .ToList();

        Assert.Equal(3, starts.Count);
        Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), starts[0]);
        Assert.Equal(new DateTime(2026, 9, 14, 10, 0, 0), starts[1]);
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0), starts[2]);
    }

    [Fact]
    public void 每月最後一個週五展開三次()
    {
        var pattern = new RecurrencePattern
        {
            Frequency = FrequencyType.Monthly,
            Interval = 1,
            ByDay = new List<WeekDay> { new(DayOfWeek.Friday) },
            BySetPosition = new List<int> { -1 },
            Count = 3,
        };

        var ev = new CalendarEvent
        {
            DtStart = new CalDateTime(2026, 9, 25, 15, 0, 0),
            DtEnd = new CalDateTime(2026, 9, 25, 16, 0, 0),
            RecurrenceRule = pattern,
        };

        var starts = ev.GetOccurrences().Take(10).Select(o => o.Period.StartTime.Value).ToList();

        Assert.Equal(3, starts.Count);
        Assert.Equal(new DateTime(2026, 9, 25, 15, 0, 0), starts[0]);
        Assert.Equal(new DateTime(2026, 10, 30, 15, 0, 0), starts[1]);
        Assert.Equal(new DateTime(2026, 11, 27, 15, 0, 0), starts[2]);
    }
}
