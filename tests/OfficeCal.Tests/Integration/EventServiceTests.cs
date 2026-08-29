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
public class EventServiceTests
{
    private readonly LocalDbFixture _db;
    public EventServiceTests(LocalDbFixture db) => _db = db;

    /// <summary>2026-09-01 09:00，讓 9 月的所有測試時段都在未來。</summary>
    private static readonly DateTime Now = new(2026, 9, 1, 9, 0, 0);

    private static DateTime D(int day, int hour) => new(2026, 9, day, hour, 0, 0);

    private static (EventService svc, FakeUserContext me) NewService(
        OfficeCalDbContext ctx, User actingAs, bool isAdmin = false)
    {
        var clock = new FixedTimeProvider(Now);
        var me = new FakeUserContext
        {
            UserId = actingAs.Id, DisplayName = actingAs.DisplayName,
            IsAdmin = isAdmin || actingAs.Role == UserRole.Admin,
        };
        var occurrences = new EventOccurrenceRepository(ctx);
        var rooms = new RoomRepository(ctx);
        var booking = new BookingService(ctx, rooms, occurrences, clock);
        var notifications = new NotificationService(ctx, new NotificationRepository(ctx), clock);

        var svc = new EventService(ctx, new EventRepository(ctx), occurrences, rooms,
                                   new UserRepository(ctx), new RecurrenceService(), booking,
                                   notifications, me, clock);
        return (svc, me);
    }

    private static CreateEventRequest Req(string title, int? roomId, int day, int hour,
                                          params int[] attendeeIds) => new()
    {
        Title = title, RoomId = roomId,
        StartAt = D(day, hour), EndAt = D(day, hour + 1),
        AttendeeIds = attendeeIds.ToList(),
    };

    [Fact]
    public async Task 建立單次事件產生一筆Occurrence並通知與會者()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var guest = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var (svc, _) = NewService(ctx, owner);

        var id = await svc.CreateAsync(Req("專案啟動會議", room.Id, 7, 10, guest.Id));

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.EventOccurrences.CountAsync(o => o.EventId == id));
        Assert.Equal(1, await verify.EventAttendees.CountAsync(a => a.EventId == id));
        var n = await verify.Notifications.SingleAsync();
        Assert.Equal(guest.Id, n.UserId);
        Assert.Equal(NotificationType.AddedToEvent, n.Type);
    }

    [Fact]
    public async Task 建立重複事件產生多筆Occurrence()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var (svc, _) = NewService(ctx, owner);

        var req = Req("週一產品例會", room.Id, 7, 10);   // 2026-09-07 是週一
        req.Recurrence = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Weekly, Interval = 1,
            ByWeekDays = new() { DayOfWeek.Monday },
            EndMode = RecurrenceEndMode.Count, Count = 4,
        };

        var id = await svc.CreateAsync(req);

        await using var verify = _db.CreateContext();
        Assert.Equal(4, await verify.EventOccurrences.CountAsync(o => o.EventId == id));
        Assert.Equal("FREQ=WEEKLY;INTERVAL=1;BYDAY=MO;COUNT=4",
                     (await verify.Events.FindAsync(id))!.RecurrenceRule);
    }

    [Fact]
    public async Task 衝突時整筆失敗且資料庫無任何寫入()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 10), D(7, 11), "季度檢討會");
        var (svc, _) = NewService(ctx, owner);

        await Assert.ThrowsAsync<ConflictException>(() => svc.CreateAsync(Req("撞期會議", room.Id, 7, 10)));

        await using var verify = _db.CreateContext();
        Assert.Equal(1, await verify.Events.CountAsync());
        Assert.Equal(1, await verify.EventOccurrences.CountAsync());
    }

    [Fact]
    public async Task 區間查詢的三種Scope()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var me = await TestData.AddUserAsync(ctx, "E001", "我");
        var other = await TestData.AddUserAsync(ctx, "E002", "別人");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        await TestData.AddBookedEventAsync(ctx, me, null, D(7, 9), D(7, 10), "我的私人事件");
        await TestData.AddBookedEventAsync(ctx, other, null, D(7, 15), D(7, 16), "別人的私人事件");
        await TestData.AddBookedEventAsync(ctx, other, room, D(7, 13), D(7, 14), "別人的會議室預約");

        var (svc, _) = NewService(ctx, me);
        var from = D(7, 0); var to = D(8, 0);

        var mine = await svc.GetRangeAsync(from, to, CalendarScope.Me, null);
        Assert.Single(mine);
        Assert.Equal("我的私人事件", mine[0].Title);

        var all = await svc.GetRangeAsync(from, to, CalendarScope.All, null);
        Assert.Single(all);
        Assert.Equal("別人的會議室預約", all[0].Title);

        var byRoom = await svc.GetRangeAsync(from, to, CalendarScope.Room, room.Id);
        Assert.Single(byRoom);

        await Assert.ThrowsAsync<ValidationException>(
            () => svc.GetRangeAsync(from, to, CalendarScope.Room, null));
    }

    [Fact]
    public async Task 純個人事件的明細他人看不到但掛會議廳的任何人可看()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var stranger = await TestData.AddUserAsync(ctx, "E002", "路人");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        var personal = await TestData.AddBookedEventAsync(ctx, owner, null, D(7, 9), D(7, 10), "私人");
        var booked = await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 13), D(7, 14), "公開");

        var (svc, _) = NewService(ctx, stranger);

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.GetDetailAsync(personal.Id));
        var detail = await svc.GetDetailAsync(booked.Id);
        Assert.Equal("公開", detail.Title);
        Assert.False(detail.CanEdit);
    }

    [Fact]
    public async Task 一般員工不能編輯他人事件()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var stranger = await TestData.AddUserAsync(ctx, "E002", "路人");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null, D(7, 9), D(7, 10));

        var (svc, _) = NewService(ctx, stranger);
        var req = new UpdateEventRequest
        {
            Title = "被亂改的標題", StartAt = D(7, 9), EndAt = D(7, 10),
        };

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.UpdateAsync(ev.Id, EditMode.Series, req));
    }

    [Fact]
    public async Task 單筆編輯不可變更會議廳()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var roomA = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var roomB = await TestData.AddRoomAsync(ctx, "B 棟 2F 小會議室");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, roomA, D(7, 10), D(7, 11));
        var occ = await ctx.EventOccurrences.FirstAsync(o => o.EventId == ev.Id);

        var (svc, _) = NewService(ctx, owner);
        var req = new UpdateEventRequest
        {
            Title = ev.Title, RoomId = roomB.Id, OccurrenceId = occ.Id,
            StartAt = D(7, 10), EndAt = D(7, 11),
        };

        var ex = await Assert.ThrowsAsync<ValidationException>(
            () => svc.UpdateAsync(ev.Id, EditMode.Single, req));
        Assert.Contains("會議廳", ex.Message);
    }

    [Fact]
    public async Task 管理員強制取消他人預約時擁有者與與會者都收到通知()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var guest = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var admin = await TestData.AddUserAsync(ctx, "A0001", "系統管理員", UserRole.Admin);
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 10), D(7, 11), "季度檢討會");
        ctx.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = guest.Id });
        await ctx.SaveChangesAsync();

        var (svc, _) = NewService(ctx, admin);
        await svc.CancelAsync(ev.Id, EditMode.Series, null);

        await using var verify = _db.CreateContext();
        Assert.Equal(EventStatus.Cancelled, (await verify.Events.FindAsync(ev.Id))!.Status);
        Assert.True(await verify.EventOccurrences.Where(o => o.EventId == ev.Id).AllAsync(o => o.IsCancelled));

        var notes = await verify.Notifications.ToListAsync();
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal(NotificationType.ForcedCancellation, n.Type));
        Assert.Contains(notes, n => n.UserId == owner.Id);
        Assert.Contains(notes, n => n.UserId == guest.Id);
    }

    [Fact]
    public async Task 與會者衝突警示回傳每人的衝突次數與標題()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var me = await TestData.AddUserAsync(ctx, "E001", "我");
        var busy = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var free = await TestData.AddUserAsync(ctx, "E003", "李小華");

        await TestData.AddBookedEventAsync(ctx, busy, null, D(7, 10), D(7, 11), "客戶拜訪");
        await TestData.AddBookedEventAsync(ctx, busy, null, D(7, 10), D(7, 12), "教育訓練");
        await TestData.AddBookedEventAsync(ctx, busy, null, D(7, 12), D(7, 13), "頭尾相接不算");

        // 規格 7.4：「擁有或被邀請的」都算。這一筆是別人主辦、busy 被邀請。
        var invited = await TestData.AddBookedEventAsync(ctx, me, null, D(7, 10), D(7, 11),
                                                          "被邀請的部門會議");
        ctx.EventAttendees.Add(new EventAttendee { EventId = invited.Id, UserId = busy.Id });
        await ctx.SaveChangesAsync();

        var (svc, _) = NewService(ctx, me);
        var result = await svc.CheckAttendeesAsync(new AttendeeConflictRequest
        {
            AttendeeIds = new() { busy.Id, free.Id },
            Slots = new() { new TimeSlotDto { StartAt = D(7, 10), EndAt = D(7, 12) } },
        });

        var b = result.Single(r => r.UserId == busy.Id);
        Assert.Equal(3, b.ConflictCount);
        Assert.Contains("客戶拜訪", b.Titles);
        Assert.Contains("被邀請的部門會議", b.Titles);
        Assert.DoesNotContain("頭尾相接不算", b.Titles);
        Assert.Equal(0, result.Single(r => r.UserId == free.Id).ConflictCount);
    }

    [Fact]
    public async Task 非擁有者非Admin不能取消他人事件()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var stranger = await TestData.AddUserAsync(ctx, "E002", "路人");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, null, D(7, 9), D(7, 10));

        var (svc, _) = NewService(ctx, stranger);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => svc.CancelAsync(ev.Id, EditMode.Series, null));
    }

    [Fact]
    public async Task 擁有者取消自己的事件走EventCancelled而非ForcedCancellation()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var guest = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var ev = await TestData.AddBookedEventAsync(ctx, owner, room, D(7, 10), D(7, 11), "季度檢討會");
        ctx.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = guest.Id });
        await ctx.SaveChangesAsync();

        var (svc, _) = NewService(ctx, owner);
        await svc.CancelAsync(ev.Id, EditMode.Series, null);

        await using var verify = _db.CreateContext();
        var notes = await verify.Notifications.ToListAsync();
        Assert.Single(notes);
        Assert.Equal(NotificationType.EventCancelled, notes[0].Type);
        Assert.Equal(guest.Id, notes[0].UserId);
    }

    /// <summary>
    /// 覆蓋 UpdateSeriesAsync 的完整路徑：重新展開 occurrence、增刪與會者、兩種通知分流。
    /// 同時涵蓋修法 3：Admin 編輯他人事件，名單含 Admin 自己時，Admin 的與會者列不能被靜默移除。
    /// </summary>
    [Fact]
    public async Task UpdateSeriesAsync完整路徑重新展開同步與會者並分流通知()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var admin = await TestData.AddUserAsync(ctx, "A0001", "系統管理員", UserRole.Admin);
        var staying = await TestData.AddUserAsync(ctx, "E002", "留下來的王小明");
        var leaving = await TestData.AddUserAsync(ctx, "E003", "離開的李小華");
        var joining = await TestData.AddUserAsync(ctx, "E004", "新加入的林大寶");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");

        // owner 建立週一例行會議，與會者含 admin 自己、staying、leaving
        var (ownerSvc, _) = NewService(ctx, owner);
        var recurrence = new RecurrencePatternDto
        {
            Frequency = RecurrenceFrequency.Weekly, Interval = 1,
            ByWeekDays = new() { DayOfWeek.Monday },
            EndMode = RecurrenceEndMode.Count, Count = 4,
        };
        var createReq = Req("週一產品例會", room.Id, 7, 10, admin.Id, staying.Id, leaving.Id);
        createReq.Recurrence = recurrence;
        var eventId = await ownerSvc.CreateAsync(createReq);

        // admin（非擁有者）編輯這個系列：改時間、拿掉 leaving、加入 joining，名單仍含 admin 自己
        await using var ctx2 = _db.CreateContext();
        var (adminSvc, _) = NewService(ctx2, admin);
        var updateReq = new UpdateEventRequest
        {
            Title = "週一產品例會", RoomId = room.Id,
            StartAt = D(7, 14), EndAt = D(7, 15),
            AttendeeIds = new() { admin.Id, staying.Id, joining.Id },
            Recurrence = recurrence,
        };
        await adminSvc.UpdateAsync(eventId, EditMode.Series, updateReq);

        await using var verify = _db.CreateContext();

        // occurrence 已重新展開到新時間
        var occStarts = await verify.EventOccurrences.Where(o => o.EventId == eventId)
                                                      .Select(o => o.StartAt).ToListAsync();
        Assert.Equal(4, occStarts.Count);
        Assert.All(occStarts, s => Assert.Equal(14, s.Hour));

        // EventAttendee 已同步：admin 自己沒被靜默移除、leaving 被移除、joining 被加入
        var attendeeIds = await verify.EventAttendees.Where(a => a.EventId == eventId)
                                                      .Select(a => a.UserId).ToListAsync();
        Assert.Contains(admin.Id, attendeeIds);
        Assert.Contains(staying.Id, attendeeIds);
        Assert.Contains(joining.Id, attendeeIds);
        Assert.DoesNotContain(leaving.Id, attendeeIds);

        // 通知分流：建立時 3 筆 AddedToEvent（admin/staying/leaving），
        // 更新時留下來的舊與會者（admin、staying）收 EventUpdated，新加入的（joining）收 AddedToEvent
        var notes = await verify.Notifications.Where(n => n.EventId == eventId).ToListAsync();
        var addedNotes = notes.Where(n => n.Type == NotificationType.AddedToEvent).ToList();
        var updatedNotes = notes.Where(n => n.Type == NotificationType.EventUpdated).ToList();

        Assert.Equal(4, addedNotes.Count);
        Assert.Contains(addedNotes, n => n.UserId == joining.Id);

        Assert.Equal(2, updatedNotes.Count);
        Assert.Contains(updatedNotes, n => n.UserId == admin.Id);
        Assert.Contains(updatedNotes, n => n.UserId == staying.Id);
        Assert.DoesNotContain(updatedNotes, n => n.UserId == leaving.Id);
        Assert.DoesNotContain(updatedNotes, n => n.UserId == joining.Id);
    }

    [Fact]
    public async Task 系列僅改標題不產生通知()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();
        var owner = await TestData.AddUserAsync(ctx, "E001", "陳大明");
        var guest = await TestData.AddUserAsync(ctx, "E002", "王小明");
        var room = await TestData.AddRoomAsync(ctx, "A 棟 3F 大會議廳");
        var (svc, _) = NewService(ctx, owner);

        var id = await svc.CreateAsync(Req("週一產品例會", room.Id, 7, 10, guest.Id));

        await using var ctx2 = _db.CreateContext();
        var (svc2, _) = NewService(ctx2, owner);
        var req = new UpdateEventRequest
        {
            Title = "改過的標題", RoomId = room.Id,
            StartAt = D(7, 10), EndAt = D(7, 11),
            AttendeeIds = new() { guest.Id },
        };
        await svc2.UpdateAsync(id, EditMode.Series, req);

        await using var verify = _db.CreateContext();
        Assert.Equal("改過的標題", (await verify.Events.FindAsync(id))!.Title);

        var notes = await verify.Notifications.ToListAsync();
        Assert.Single(notes);   // 只有建立時的那一筆 AddedToEvent，更新時沒有新增
        Assert.Equal(NotificationType.AddedToEvent, notes[0].Type);
    }
}
