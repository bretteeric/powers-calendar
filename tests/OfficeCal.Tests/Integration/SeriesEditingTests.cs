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

    // ---------- 規格 §11 驗收標準 4：整系列編輯「改變時間」後，被單獨處理過的那幾次仍維持原樣 ----------
    // 既有測試的整系列重新展開若不是沿用原時間（SlotsAt(10)），就是沒有被單獨處理過的列，
    // 因此都沒有踩到「新 slot 的 Start 與 survivor 的 OriginalStartAt 注定不相等」這個組合。

    [Fact]
    public async Task 整系列改變時間後被單獨取消的那次不會以新時間復活()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        var o28 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(28, 10));
        o28.IsCancelled = true;
        await ctx.SaveChangesAsync();

        // 整系列 10:00 → 11:00
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(11)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        // 9/28 只有一列，仍是已取消、仍是原時間——沒有以 11:00 復活
        var d28 = Assert.Single(all, o => o.OriginalStartAt.Date == D(28, 0).Date);
        Assert.True(d28.IsCancelled);
        Assert.Equal(D(28, 10), d28.StartAt);
        // 沒被單獨處理過的 9/21 照常改成新時間
        Assert.Equal(D(21, 11), all.Single(o => o.OriginalStartAt == D(21, 11)).StartAt);
    }

    [Fact]
    public async Task 整系列改變時間後被單獨修改的那次維持原樣且同一天不重複()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(21, 14); o21.EndAt = D(21, 15); o21.IsModified = true;
        await ctx.SaveChangesAsync();

        // 整系列 10:00 → 11:00
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(11)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        // 9/21 只有一列，內容與單獨修改後完全一致
        var d21 = Assert.Single(all, o => o.OriginalStartAt.Date == D(21, 0).Date);
        Assert.Equal(o21.Id, d21.Id);
        Assert.Equal(D(21, 14), d21.StartAt);
        Assert.Equal(D(21, 15), d21.EndAt);
        Assert.True(d21.IsModified);
        Assert.False(d21.IsCancelled);
        // 沒被單獨處理過的 9/28 照常改成新時間（證明正常重新展開沒被誤擋）
        Assert.Equal(D(28, 11), all.Single(o => o.OriginalStartAt == D(28, 11)).StartAt);
    }

    [Fact]
    public async Task 先單獨修改再單獨取消後整系列改變時間不重複也不復活()
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

        // 整系列 10:00 → 11:00：兩個未來日期都已被單獨處理過，不該產生任何新列
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(11)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(4, all.Count);                       // 沒有多長出任何一列
        Assert.Equal(D(7, 10), all[0].StartAt);           // 已發生，不改寫歷史
        Assert.Equal(D(14, 10), all[1].StartAt);

        var d21 = Assert.Single(all, o => o.OriginalStartAt.Date == D(21, 0).Date);
        Assert.Equal(o21.Id, d21.Id);                     // 被單獨修改的那列原封不動
        Assert.Equal(D(21, 14), d21.StartAt);
        Assert.True(d21.IsModified);

        var d28 = Assert.Single(all, o => o.OriginalStartAt.Date == D(28, 0).Date);
        Assert.Equal(o28.Id, d28.Id);                     // 被單獨取消的那列沒有復活
        Assert.True(d28.IsCancelled);
        Assert.Equal(D(28, 10), d28.StartAt);
    }

    [Fact]
    public async Task 整系列改到今天稍晚時已發生過的那次不會在同一天長出第二列()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");

        // 每日 10:00–11:00，9/14、9/15、9/16 各一次。now = 9/15 12:00，故 9/15 10:00 已發生過。
        var ev = new Event
        {
            Title = "每日站立會議", OwnerId = owner.Id, RoomId = null,
            StartAt = D(14, 10), EndAt = D(14, 11),
            RecurrenceRule = "FREQ=DAILY;INTERVAL=1;COUNT=3",
            CreatedAt = D(1, 9), UpdatedAt = D(1, 9),
        };
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();
        foreach (var day in new[] { 14, 15, 16 })
        {
            ctx.EventOccurrences.Add(new EventOccurrence
            {
                EventId = ev.Id, OriginalStartAt = D(day, 10),
                StartAt = D(day, 10), EndAt = D(day, 11),
            });
        }
        await ctx.SaveChangesAsync();

        // 整系列改到 15:00：今天（9/15）的 15:00 仍在未來，精確比對擋不住它
        var slots = new[] { 14, 15, 16 }.Select(d => new TimeSlot(D(d, 15), D(d, 16))).ToList();
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, slots));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(3, all.Count);
        Assert.Equal(D(14, 10), all[0].StartAt);          // 已發生
        // 今天只有一列，就是已經發生過的那次；不會再冒出一個 15:00
        var today = Assert.Single(all, o => o.OriginalStartAt.Date == D(15, 0).Date);
        Assert.Equal(D(15, 10), today.StartAt);
        Assert.Equal(D(16, 15), all.Single(o => o.OriginalStartAt == D(16, 15)).StartAt);
    }

    [Fact]
    public async Task 被單獨移到別的日期時目標日期的系列實例仍會建立()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var ev = await AddWeeklySeriesAsync(ctx, owner, null);

        // 9/21 那次被單獨搬到 9/28 14:00（OriginalStartAt 仍是 9/21 10:00——身分不變）
        var o21 = await ctx.EventOccurrences.FirstAsync(o => o.OriginalStartAt == D(21, 10));
        o21.StartAt = D(28, 14); o21.EndAt = D(28, 15); o21.IsModified = true;
        await ctx.SaveChangesAsync();

        // 整系列 10:00 → 11:00
        await InTransactionAsync(ctx, () => NewService(ctx).ReExpandSeriesAsync(ev, SlotsAt(11)));

        await using var verify = _db.CreateContext();
        var all = await verify.EventOccurrences.OrderBy(o => o.OriginalStartAt).ToListAsync();

        Assert.Equal(4, all.Count);
        // 去重的鍵是 OriginalStartAt（發生的身分），不是 StartAt（目前落點）：
        // 9/21 的身分已被保留 → 不再建立；9/28 的身分沒被保留 → 照常建立。
        var moved = Assert.Single(all, o => o.OriginalStartAt == D(21, 10));
        Assert.Equal(D(28, 14), moved.StartAt);
        Assert.True(moved.IsModified);
        Assert.DoesNotContain(all, o => o.OriginalStartAt == D(21, 11));
        // 使用者刻意把兩者疊在 9/28 —— 目標日期上出現兩列是正確的
        Assert.Equal(2, all.Count(o => o.StartAt.Date == D(28, 0).Date));
        Assert.Equal(D(28, 11), all.Single(o => o.OriginalStartAt == D(28, 11)).StartAt);
    }
}
