using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class SeriesEditingTests
{
    private readonly LocalDbFixture _db;
    public SeriesEditingTests(LocalDbFixture db) => _db = db;

    /// <summary>2026-09-15（週二）12:00 —— 讓 9/7、9/14 落在過去，9/21、9/28 落在未來。</summary>
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0);

    private static DateTime D(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    private static BookingService NewService(OfficeCalDbContext ctx)
        => new(ctx, new RoomRepository(ctx), new EventOccurrenceRepository(ctx),
               new FixedTimeProvider(Now));

    /// <summary>建立每週一 10:00–11:00、共四次（9/7、9/14、9/21、9/28）的系列。</summary>
    private static async Task<Event> AddWeeklySeriesAsync(OfficeCalDbContext db, User owner, Room? room)
    {
        var ev = new Event
        {
            Title = "週一產品例會", OwnerId = owner.Id, RoomId = room?.Id,
            StartAt = D(7, 10), EndAt = D(7, 11),
            RecurrenceRule = "FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;COUNT=4",
            CreatedAt = D(1, 9), UpdatedAt = D(1, 9),
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        foreach (var day in new[] { 7, 14, 21, 28 })
        {
            db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = ev.Id, OriginalStartAt = D(day, 10),
                StartAt = D(day, 10), EndAt = D(day, 11), RoomId = room?.Id,
            });
        }
        await db.SaveChangesAsync();
        return ev;
    }

    private static IReadOnlyList<TimeSlot> SlotsAt(int hour)
        => new[] { 7, 14, 21, 28 }.Select(d => new TimeSlot(D(d, hour), D(d, hour + 1))).ToList();

    private static async Task InTransactionAsync(OfficeCalDbContext ctx, Func<Task> body)
    {
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await body();
        await tx.CommitAsync();
    }

    [Fact]
    public async Task 重新展開保留被單獨修改與被單獨取消的發生()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(21, 14); o21.EndAt = D(21, 15); o21.IsModified = true;
        var o28 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(28, 10));
        o28.IsCancelled = true;
        await ctx.SaveChangesAsync();

        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        Assert.Equal(D(21, 14), all.Single(o => o.OriginalStartAt == D(21, 10)).StartAt);
        Assert.True(all.Single(o => o.OriginalStartAt == D(28, 10)).IsCancelled);
    }

    [Fact]
    public async Task 重新展開不改動已發生過的發生()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        // 整個系列改到 14:00
        ev.StartAt = D(7, 14); ev.EndAt = D(7, 15);
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(14)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.StartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        Assert.Equal(D(7, 10), all[0].StartAt);    // 已發生，維持 10:00
        Assert.Equal(D(14, 10), all[1].StartAt);   // 已發生，維持 10:00
        Assert.Equal(D(21, 14), all[2].StartAt);   // 未來，改為 14:00
        Assert.Equal(D(28, 14), all[3].StartAt);
    }

    [Fact]
    public async Task 重新展開不會為已保留的發生日期重複插入()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.IsCancelled = true;
        await ctx.SaveChangesAsync();

        // 用同一組時段重新展開：9/21 已被單獨取消，不得再長回來
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10)));

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync(o => o.OriginalStartAt == D(21, 10)));
        Assert.True(await verify.EventOccurrences
            .Where(o => o.OriginalStartAt == D(21, 10)).Select(o => o.IsCancelled).SingleAsync());
    }

    [Fact]
    public async Task 系列換會議廳時保留的未來發生一併搬過去()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await AddWeeklySeriesAsync(ctx, owner, roomA);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(21, 14); o21.EndAt = D(21, 15); o21.IsModified = true;
        await ctx.SaveChangesAsync();

        ev.RoomId = roomB.Id;
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(roomA.Id, all.Single(o => o.OriginalStartAt == D(7, 10)).RoomId);   // 過去不動
        Assert.Equal(roomB.Id, all.Single(o => o.OriginalStartAt == D(21, 10)).RoomId);  // 保留的未來搬走
        Assert.Equal(roomB.Id, all.Single(o => o.OriginalStartAt == D(28, 10)).RoomId);  // 新產生的
    }

    [Fact]
    public async Task 系列換會議廳時被保留的未來發生撞到新會議廳既有預約也整筆失敗()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await AddWeeklySeriesAsync(ctx, owner, roomA);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(21, 14); o21.EndAt = D(21, 15); o21.IsModified = true;
        await ctx.SaveChangesAsync();

        // roomB 在保留列被搬過去的新時段（9/21 14:00–15:00）已經有別人的預約
        await TestData.AddBookedEventAsync(ctx, other, roomB, D(21, 14), D(21, 15), "季度檢討會");

        ev.RoomId = roomB.Id;
        await Assert.ThrowsAsync<ConflictException>(() =>
            InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10))));

        await using var verify = _db.CreateContext();
        // 交易已回滾：原系列仍在 roomA，含被單獨改期的那筆（StartAt 14:00，RoomId 仍是 roomA）
        Assert.Equal(4, await verify.EventOccurrences.CountAsync(o => o.RoomId == roomA.Id));
        Assert.Equal(D(21, 14),
            (await verify.EventOccurrences.SingleAsync(o => o.OriginalStartAt == D(21, 10))).StartAt);
    }

    [Fact]
    public async Task 系列換到已被占用的會議廳時整筆失敗()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await AddWeeklySeriesAsync(ctx, owner, roomA);
        await TestData.AddBookedEventAsync(ctx, other, roomB, D(28, 10), D(28, 11), "季度檢討會");

        ev.RoomId = roomB.Id;
        await Assert.ThrowsAsync<ConflictException>(() =>
            InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(10))));

        await using var verify = _db.CreateContext();
        // 交易已回滾：原系列仍在 roomA
        Assert.Equal(4, await verify.EventOccurrences.CountAsync(o => o.RoomId == roomA.Id));
    }

    [Fact]
    public async Task 單筆改期撞到既有預約時回衝突()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await AddWeeklySeriesAsync(ctx, owner, room);
        await TestData.AddBookedEventAsync(ctx, other, room, D(21, 14), D(21, 15), "季度檢討會");

        var o21 = await ctx.EventOccurrences
            .FirstAsync(o => o.OriginalStartAt == D(21, 10) && o.RoomId == room.Id);

        await Assert.ThrowsAsync<ConflictException>(() => InTransactionAsync(ctx,
            () => NewService(ctx).MoveOccurrenceAsync(o21, D(21, 14), D(21, 15))));

        await using var verify = _db.CreateContext();
        Assert.Equal(D(21, 10), (await verify.EventOccurrences.FindAsync(o21.Id))!.StartAt);
    }

    [Fact]
    public async Task 單筆改期到空檔時成功並標記為已修改()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await AddWeeklySeriesAsync(ctx, owner, room);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        await InTransactionAsync(ctx,
            () => NewService(ctx).MoveOccurrenceAsync(o21, D(21, 14), D(21, 15)));

        await using var verify = _db.CreateContext();
        var moved = await verify.EventOccurrences.FindAsync(o21.Id);
        Assert.Equal(D(21, 14), moved!.StartAt);
        Assert.Equal(D(21, 10), moved.OriginalStartAt);   // 身分不變
        Assert.True(moved.IsModified);
    }

    [Fact]
    public async Task 僅改標題不需要交易也不做衝突檢查()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var other = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await AddWeeklySeriesAsync(ctx, owner, room);
        // 同一時段另有一筆預約也無妨——改標題不碰時段
        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));

        await NewService(ctx).SetOccurrenceTitleAsync(o21, "改期前的臨時議題");

        await using var verify = _db.CreateContext();
        var updated = await verify.EventOccurrences.FindAsync(o21.Id);
        Assert.Equal("改期前的臨時議題", updated!.TitleOverride);
        Assert.True(updated.IsModified);
        Assert.Equal(D(21, 10), updated.StartAt);
    }
}
