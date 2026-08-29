using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class BookingServiceTests
{
    private readonly LocalDbFixture _db;
    public BookingServiceTests(LocalDbFixture db) => _db = db;

    private static DateTime T(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    private static BookingService NewService(OfficeCalDbContext ctx)
        => new(ctx, new RoomRepository(ctx), new EventOccurrenceRepository(ctx),
               new FixedTimeProvider(new DateTime(2026, 9, 1, 0, 0, 0)));

    private static Event NewEvent(User owner, Room? room, DateTime start, DateTime end,
                                  string title = "新會議")
        => new()
        {
            Title = title, OwnerId = owner.Id, RoomId = room?.Id,
            StartAt = start, EndAt = end,
            CreatedAt = start, UpdatedAt = start,
        };

    /// <summary>模擬 EventService：開交易、存 Event 取得 Id、呼叫 BookingService、提交。</summary>
    private static async Task BookAsync(OfficeCalDbContext ctx, Event ev, IReadOnlyList<TimeSlot> slots)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync();
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();
        await NewService(ctx).CreateOccurrencesAsync(ev, slots);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task 未指派會議廳的事件不做衝突檢查()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");

        await TestData.AddBookedEventAsync(ctx, owner, null, T(14, 10), T(14, 11), "既有個人事件");
        var ev = NewEvent(owner, null, T(14, 10), T(14, 11));

        await BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) });

        Assert.Equal(2, await ctx.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 會議廳時段重疊時丟出衝突例外且不寫入任何資料()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "季度檢討會");

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) }));

        Assert.Single(ex.Conflicts);
        Assert.Equal("季度檢討會", ex.Conflicts[0].Title);
        Assert.Equal("陳大明", ex.Conflicts[0].OwnerName);
        Assert.Equal("A 棟 3F 大會議廳", ex.Conflicts[0].RoomName);

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync());   // 只有既有那一筆
        Assert.Equal(1, await verify.Events.CountAsync());             // 新 Event 也已回滾
    }

    [Fact]
    public async Task 頭尾相接不算衝突()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 9), T(14, 10));

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        await BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) });

        Assert.Equal(2, await ctx.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 已取消的Occurrence不參與衝突判定()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), cancelled: true);

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        await BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) });

        Assert.Equal(1, await ctx.EventOccurrences.CountAsync(o => !o.IsCancelled));
    }

    [Fact]
    public async Task 重複事件中任一次衝突就整筆失敗()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        // 只有第三次（9/21）撞到
        await TestData.AddBookedEventAsync(ctx, owner, room, T(21, 10), T(21, 11), "既有會議");

        var ev = NewEvent(owner, room, T(7, 10), T(7, 11), "週一產品例會");
        var slots = new[]
        {
            new TimeSlot(T(7, 10), T(7, 11)),
            new TimeSlot(T(14, 10), T(14, 11)),
            new TimeSlot(T(21, 10), T(21, 11)),
        };

        var ex = await Assert.ThrowsAsync<ConflictException>(() => BookAsync(ctx, ev, slots));
        Assert.Single(ex.Conflicts);

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 停用的會議廳不可新增預約()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "已停用的會議廳", isActive: false);

        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        await Assert.ThrowsAsync<ValidationException>(
            () => BookAsync(ctx, ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) }));
    }

    [Fact]
    public async Task 沒有交易就呼叫會被擋下()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = NewEvent(owner, room, T(14, 10), T(14, 11));
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(ctx).CreateOccurrencesAsync(ev, new[] { new TimeSlot(T(14, 10), T(14, 11)) }));
    }

    [Fact]
    public async Task 取消單一次發生只影響那一筆()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = NewEvent(owner, room, T(7, 10), T(7, 11), "週一產品例會");
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();

        var occ1 = new EventOccurrence
        {
            EventId = ev.Id, OriginalStartAt = T(7, 10), StartAt = T(7, 10), EndAt = T(7, 11), RoomId = room.Id,
        };
        var occ2 = new EventOccurrence
        {
            EventId = ev.Id, OriginalStartAt = T(14, 10), StartAt = T(14, 10), EndAt = T(14, 11), RoomId = room.Id,
        };
        ctx.EventOccurrences.AddRange(occ1, occ2);
        await ctx.SaveChangesAsync();

        await NewService(ctx).CancelOccurrenceAsync(occ1);

        await using var verify = _db.CreateContext();
        var cancelled = await verify.EventOccurrences.SingleAsync(o => o.Id == occ1.Id);
        var other = await verify.EventOccurrences.SingleAsync(o => o.Id == occ2.Id);
        Assert.True(cancelled.IsCancelled);
        Assert.False(other.IsCancelled);
        var eventAfter = await verify.Events.SingleAsync(e => e.Id == ev.Id);
        Assert.Equal(EventStatus.Active, eventAfter.Status);
    }

    [Fact]
    public async Task 取消整個系列會同時改動Event與所有occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = NewEvent(owner, room, T(7, 10), T(7, 11), "週一產品例會");
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();

        ctx.EventOccurrences.AddRange(
            new EventOccurrence
            {
                EventId = ev.Id, OriginalStartAt = T(7, 10), StartAt = T(7, 10), EndAt = T(7, 11), RoomId = room.Id,
            },
            new EventOccurrence
            {
                EventId = ev.Id, OriginalStartAt = T(14, 10), StartAt = T(14, 10), EndAt = T(14, 11), RoomId = room.Id,
            },
            new EventOccurrence
            {
                EventId = ev.Id, OriginalStartAt = T(21, 10), StartAt = T(21, 10), EndAt = T(21, 11), RoomId = room.Id,
            });
        await ctx.SaveChangesAsync();

        await NewService(ctx).CancelSeriesAsync(ev);

        await using var verify = _db.CreateContext();
        var eventAfter = await verify.Events.SingleAsync(e => e.Id == ev.Id);
        Assert.Equal(EventStatus.Cancelled, eventAfter.Status);

        var occurrences = await verify.EventOccurrences.Where(o => o.EventId == ev.Id).ToListAsync();
        Assert.Equal(3, occurrences.Count);
        Assert.All(occurrences, o => Assert.True(o.IsCancelled));
    }
}
