using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class RruleFormatterTests
{
    private static RecurrencePatternDto 每週一() => new()
    {
        Frequency = RecurrenceFrequency.Weekly,
        Interval = 1,
        ByWeekDays = new() { DayOfWeek.Monday },
        EndMode = RecurrenceEndMode.UntilDate,
        UntilDate = new DateOnly(2026, 12, 28),
    };

    private static RecurrencePatternDto 每月最後一個週五() => new()
    {
        Frequency = RecurrenceFrequency.Monthly,
        Interval = 1,
        MonthlyMode = MonthlyMode.WeekDayOfMonth,
        BySetPosition = -1,
        ByPositionWeekDay = DayOfWeek.Friday,
        EndMode = RecurrenceEndMode.Count,
        Count = 12,
    };

    private static RecurrencePatternDto 每兩週的週二與週四() => new()
    {
        Frequency = RecurrenceFrequency.Weekly,
        Interval = 2,
        ByWeekDays = new() { DayOfWeek.Tuesday, DayOfWeek.Thursday },
        EndMode = RecurrenceEndMode.Count,
        Count = 10,
    };

    private static RecurrencePatternDto 每年九月十五日() => new()
    {
        Frequency = RecurrenceFrequency.Yearly,
        Interval = 1,
        ByMonth = 9,
        ByMonthDay = 15,
        EndMode = RecurrenceEndMode.Count,
        Count = 5,
    };

    [Fact]
    public void 每週一轉出正確的RRULE()
        => Assert.Equal("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;UNTIL=20261228T235959",
                        RruleFormatter.ToRrule(每週一()));

    [Fact]
    public void 每月最後一個週五轉出正確的RRULE()
        => Assert.Equal("FREQ=MONTHLY;INTERVAL=1;BYDAY=FR;BYSETPOS=-1;COUNT=12",
                        RruleFormatter.ToRrule(每月最後一個週五()));

    [Fact]
    public void 每兩週的週二與週四轉出正確的RRULE()
        => Assert.Equal("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH;COUNT=10",
                        RruleFormatter.ToRrule(每兩週的週二與週四()));

    [Fact]
    public void 每年九月十五日轉出正確的RRULE()
        => Assert.Equal("FREQ=YEARLY;INTERVAL=1;BYMONTH=9;BYMONTHDAY=15;COUNT=5",
                        RruleFormatter.ToRrule(每年九月十五日()));

    [Fact]
    public void 每月十五日轉出正確的RRULE()
    {
        var dto = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            MonthlyMode = MonthlyMode.DayOfMonth,
            ByMonthDay = 15,
            EndMode = RecurrenceEndMode.Count,
            Count = 6,
        };
        Assert.Equal("FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15;COUNT=6", RruleFormatter.ToRrule(dto));
    }

    [Theory]
    [MemberData(nameof(所有樣本))]
    public void 雙向轉換可還原(RecurrencePatternDto original)
    {
        var rrule = RruleFormatter.ToRrule(original);
        var parsed = RruleFormatter.Parse(rrule);
        Assert.Equal(rrule, RruleFormatter.ToRrule(parsed));
        Assert.Equal(original.Frequency, parsed.Frequency);
        Assert.Equal(original.Interval, parsed.Interval);
        Assert.Equal(original.ByWeekDays, parsed.ByWeekDays);
        Assert.Equal(original.MonthlyMode, parsed.MonthlyMode);
        Assert.Equal(original.ByMonthDay, parsed.ByMonthDay);
        Assert.Equal(original.BySetPosition, parsed.BySetPosition);
        Assert.Equal(original.ByPositionWeekDay, parsed.ByPositionWeekDay);
        Assert.Equal(original.ByMonth, parsed.ByMonth);
        Assert.Equal(original.EndMode, parsed.EndMode);
        Assert.Equal(original.UntilDate, parsed.UntilDate);
        Assert.Equal(original.Count, parsed.Count);
    }

    public static TheoryData<RecurrencePatternDto> 所有樣本() => new()
    {
        每週一(), 每月最後一個週五(), 每兩週的週二與週四(), 每年九月十五日(),
    };

    [Fact]
    public void 沒有結束條件的規則被拒絕()
    {
        var dto = 每週一();
        dto.EndMode = RecurrenceEndMode.UntilDate;
        dto.UntilDate = null;
        var ex = Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
        Assert.Contains("結束", ex.Message);
    }

    [Fact]
    public void 沒有指定次數的Count模式被拒絕()
    {
        var dto = 每週一();
        dto.EndMode = RecurrenceEndMode.Count;
        dto.Count = null;
        Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
    }

    [Fact]
    public void Count超過上限被拒絕()
    {
        var dto = 每週一();
        dto.EndMode = RecurrenceEndMode.Count;
        dto.Count = 731;
        var ex = Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void 每週規則未勾選任何星期被拒絕()
    {
        var dto = 每週一();
        dto.ByWeekDays = new();
        Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
    }

    [Fact]
    public void 間隔小於一被拒絕()
    {
        var dto = 每週一();
        dto.Interval = 0;
        Assert.Throws<ValidationException>(() => RruleFormatter.ToRrule(dto));
    }

    [Fact]
    public void 缺少FREQ的字串解析失敗()
        => Assert.Throws<ValidationException>(() => RruleFormatter.Parse("INTERVAL=1;COUNT=3"));
}
