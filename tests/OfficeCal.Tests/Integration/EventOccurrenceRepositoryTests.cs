using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class EventOccurrenceRepositoryTests
{
    private readonly LocalDbFixture _db;
    public EventOccurrenceRepositoryTests(LocalDbFixture db) => _db = db;

    private static DateTime T(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    [Fact]
    public async Task 會議廳查詢排除已取消與頭尾相接的時段()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "重疊的會議");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 11), T(14, 12), "頭尾相接的會議");
        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "已取消的會議",
                                           cancelled: true);

        var repo = new EventOccurrenceRepository(ctx);
        var found = await repo.GetRoomOccurrencesAsync(room.Id, T(14, 10), T(14, 11), null);

        Assert.Single(found);
        Assert.Equal("重疊的會議", found[0].Event!.Title);
    }

    [Fact]
    public async Task 會議廳查詢可排除指定事件自己的Occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11));

        var repo = new EventOccurrenceRepository(ctx);

        Assert.Single(await repo.GetRoomOccurrencesAsync(room.Id, T(14, 10), T(14, 11), null));
        Assert.Empty(await repo.GetRoomOccurrencesAsync(room.Id, T(14, 10), T(14, 11), ev.Id));
    }

    [Fact]
    public async Task 個人範圍查詢涵蓋擁有與被邀請但不含他人的私人事件()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var me = await TestData.AddUserAsync(ctx, "E001", "我");
        var other = await TestData.AddUserAsync(ctx, "E002", "別人");

        await TestData.AddBookedEventAsync(ctx, me, null, T(14, 9), T(14, 10), "我的私人事件");
        var invited = await TestData.AddBookedEventAsync(ctx, other, null, T(14, 13), T(14, 14),
                                                         "我被邀請的事件");
        ctx.EventAttendees.Add(new OfficeCal.Core.Entities.EventAttendee
        {
            EventId = invited.Id, UserId = me.Id,
        });
        await ctx.SaveChangesAsync();
        await TestData.AddBookedEventAsync(ctx, other, null, T(14, 15), T(14, 16), "別人的私人事件");

        var repo = new EventOccurrenceRepository(ctx);
        var mine = await repo.GetRangeForUserAsync(me.Id, T(14, 0), T(15, 0));

        Assert.Equal(2, mine.Count);
        Assert.DoesNotContain(mine, o => o.Event!.Title == "別人的私人事件");
    }

    [Fact]
    public async Task 全域範圍查詢只回傳已掛會議廳的Occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        await TestData.AddBookedEventAsync(ctx, owner, room, T(14, 10), T(14, 11), "會議室預約");
        await TestData.AddBookedEventAsync(ctx, owner, null, T(14, 10), T(14, 11), "純個人事件");

        var repo = new EventOccurrenceRepository(ctx);
        var all = await repo.GetRangeAllRoomsAsync(T(14, 0), T(15, 0));

        Assert.Single(all);
        Assert.Equal("會議室預約", all[0].Event!.Title);
    }
}
