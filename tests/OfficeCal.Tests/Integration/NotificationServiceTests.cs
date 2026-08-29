using Microsoft.EntityFrameworkCore;
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
public class NotificationServiceTests
{
    private readonly LocalDbFixture _db;
    public NotificationServiceTests(LocalDbFixture db) => _db = db;

    private static readonly DateTime Now = new(2026, 9, 1, 9, 0, 0);

    private static NotificationService NewService(OfficeCalDbContext ctx)
        => new(ctx, new NotificationRepository(ctx), new FixedTimeProvider(Now));

    [Fact]
    public async Task 建立事件通知全體與會者但不含擁有者()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var b = await TestData.AddUserAsync(ctx, "E003", "李小華");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0), "週一產品例會");

        await NewService(ctx).AddedToEventAsync(ev, new DateTime(2026, 9, 7, 10, 0, 0),
                                                new[] { owner.Id, a.Id, b.Id }, "陳大明");

        await using var verify = _db.CreateContext();
        var all = await verify.Notifications.ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.DoesNotContain(all, n => n.UserId == owner.Id);
        Assert.All(all, n => Assert.Equal(NotificationType.AddedToEvent, n.Type));
        Assert.All(all, n => Assert.Contains("週一產品例會", n.Message));
        Assert.All(all, n => Assert.Contains("9/7 10:00", n.Message));
    }

    [Fact]
    public async Task 單筆改期的通知標明是哪一次發生()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0), "週一產品例會");

        await NewService(ctx).EventUpdatedAsync(ev, new[] { a.Id },
            occurrenceOriginalStart: new DateTime(2026, 9, 14, 10, 0, 0),
            newStart: new DateTime(2026, 9, 14, 14, 0, 0),
            roomName: null);

        await using var verify = _db.CreateContext();
        var n = await verify.Notifications.SingleAsync();

        Assert.Equal(NotificationType.EventUpdated, n.Type);
        Assert.Contains("9/14", n.Message);
        Assert.Contains("週一產品例會", n.Message);
        Assert.Contains("14:00", n.Message);
    }

    [Fact]
    public async Task 強制取消同時通知擁有者與與會者()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0), "季度檢討會");

        await NewService(ctx).ForcedCancellationAsync(ev, new[] { owner.Id, a.Id },
                                                      occurrenceStart: null, adminName: "系統管理員");

        await using var verify = _db.CreateContext();
        var all = await verify.Notifications.ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, n => Assert.Equal(NotificationType.ForcedCancellation, n.Type));
        Assert.All(all, n => Assert.Contains("系統管理員", n.Message));
    }

    [Fact]
    public async Task 清單可只取未讀且標記已讀只有收件者本人可以做()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var a = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var b = await TestData.AddUserAsync(ctx, "E003", "李小華");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null,
            new DateTime(2026, 9, 7, 10, 0, 0), new DateTime(2026, 9, 7, 11, 0, 0));

        var svc = NewService(ctx);
        await svc.AddedToEventAsync(ev, new DateTime(2026, 9, 7, 10, 0, 0),
                                    new[] { a.Id }, "陳大明");

        var list = await svc.ListAsync(a.Id, unreadOnly: true, take: 20);
        Assert.Single(list);

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.MarkReadAsync(list[0].Id, b.Id));

        await svc.MarkReadAsync(list[0].Id, a.Id);
        Assert.Empty(await svc.ListAsync(a.Id, unreadOnly: true, take: 20));
        Assert.Single(await svc.ListAsync(a.Id, unreadOnly: false, take: 20));
        Assert.Equal(0, await svc.UnreadCountAsync(a.Id));
    }
}
