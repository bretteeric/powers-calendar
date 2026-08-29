using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Services;
using Xunit;

namespace OfficeCal.Tests.Unit;

public class RecurrenceServiceTests
{
    private readonly IRecurrenceService _svc = new RecurrenceService();

    [Fact]
    public void 單次事件展開為一筆()
    {
        var slots = _svc.Expand(null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0));

        Assert.Single(slots);
        Assert.Equal(new DateTime(2026, 9, 7, 10, 0, 0), slots[0].Start);
        Assert.Equal(new DateTime(2026, 9, 7, 11, 0, 0), slots[0].End);
    }

    [Fact]
    public void 每週一展開時每筆長度都等於首次長度()
    {
        var rrule = "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;COUNT=3";
        var slots = _svc.Expand(rrule,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 30, 0));

        Assert.Equal(3, slots.Count);
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(90), s.End - s.Start));
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0), slots[2].Start);
    }

    [Fact]
    public void 跨年展開正確()
    {
        var rrule = "FREQ=MONTHLY;INTERVAL=1;BYMONTHDAY=15;COUNT=4";
        var slots = _svc.Expand(rrule,
            new DateTime(2026, 11, 15, 9, 0, 0), new DateTime(2026, 11, 15, 10, 0, 0));

        Assert.Equal(4, slots.Count);
        Assert.Equal(new DateTime(2026, 12, 15, 9, 0, 0), slots[1].Start);
        Assert.Equal(new DateTime(2027, 1, 15, 9, 0, 0), slots[2].Start);
        Assert.Equal(new DateTime(2027, 2, 15, 9, 0, 0), slots[3].Start);
    }

    [Fact]
    public void UNTIL為含當日的邊界()
    {
        // 2026-09-07 是週一；UNTIL=2026-09-21 應含 9/21 當天那一次。
        var rrule = "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;UNTIL=20260921T235959";
        var slots = _svc.Expand(rrule,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0));

        Assert.Equal(3, slots.Count);
        Assert.Equal(new DateTime(2026, 9, 21, 10, 0, 0), slots[^1].Start);
    }

    [Fact]
    public void 展開超過上限被拒絕()
    {
        // 每天一次、UNTIL 在三年後 → 遠超過 730 筆
        var rrule = "FREQ=DAILY;INTERVAL=1;UNTIL=20291231T235959";
        var ex = Assert.Throws<ValidationException>(() => _svc.Expand(rrule,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0)));

        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public void 沒有結束條件的規則被拒絕()
        => Assert.Throws<ValidationException>(() => _svc.Expand("FREQ=DAILY;INTERVAL=1",
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0)));

    [Fact]
    public void 起始日與每週規則不符時被拒絕()
    {
        // 2026-09-08 是週二，規則卻是每週一
        var p = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Weekly,
            Interval = 1,
            ByWeekDays = new() { DayOfWeek.Monday },
            EndMode = RecurrenceEndMode.Count,
            Count = 3,
        };
        var ex = Assert.Throws<ValidationException>(
            () => _svc.ValidateStartMatches(p, new DateTime(2026, 9, 8, 10, 0, 0)));
        Assert.Contains("起始日", ex.Message);
    }

    [Fact]
    public void 起始日為每月最後一個週五時通過驗證()
    {
        var p = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Monthly,
            Interval = 1,
            MonthlyMode = MonthlyMode.WeekDayOfMonth,
            BySetPosition = -1,
            ByPositionWeekDay = DayOfWeek.Friday,
            EndMode = RecurrenceEndMode.Count,
            Count = 3,
        };
        // 2026-09-25 是九月的最後一個週五
        _svc.ValidateStartMatches(p, new DateTime(2026, 9, 25, 15, 0, 0));
    }

    [Fact]
    public void 結構化設定經由服務也能轉出RRULE()
    {
        var p = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Daily,
            Interval = 3,
            EndMode = RecurrenceEndMode.Count,
            Count = 5,
        };
        Assert.Equal("FREQ=DAILY;INTERVAL=3;COUNT=5", _svc.ToRrule(p));
        Assert.Equal(RecurrenceFrequency.Daily, _svc.ParseRrule("FREQ=DAILY;INTERVAL=3;COUNT=5").Frequency);
    }
}
