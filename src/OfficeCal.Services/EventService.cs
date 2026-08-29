using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class EventService : IEventService
{
    private readonly OfficeCalDbContext _db;
    private readonly IEventRepository _events;
    private readonly IEventOccurrenceRepository _occurrences;
    private readonly IRoomRepository _rooms;
    private readonly IUserRepository _users;
    private readonly IRecurrenceService _recurrence;
    private readonly IBookingService _booking;
    private readonly INotificationService _notifications;
    private readonly IUserContext _me;
    private readonly TimeProvider _clock;

    // 相依項偏多是編排者的本質：本服務把重複展開、衝突鎖、通知三件事縫在同一個交易裡。
    public EventService(OfficeCalDbContext db, IEventRepository events,
                        IEventOccurrenceRepository occurrences, IRoomRepository rooms,
                        IUserRepository users, IRecurrenceService recurrence,
                        IBookingService booking, INotificationService notifications,
                        IUserContext me, TimeProvider clock)
    {
        _db = db; _events = events; _occurrences = occurrences; _rooms = rooms; _users = users;
        _recurrence = recurrence; _booking = booking; _notifications = notifications;
        _me = me; _clock = clock;
    }

    // ---------- 建立 ----------

    public async Task<int> CreateAsync(CreateEventRequest req, CancellationToken ct = default)
    {
        var (startAt, endAt) = Normalize(req.StartAt, req.EndAt, req.IsAllDay);
        var rrule = BuildRrule(req.Recurrence, startAt);
        var slots = _recurrence.Expand(rrule, startAt, endAt);
        var attendeeIds = await ValidateAttendeesAsync(req.AttendeeIds, ct);
        var now = TaipeiTime.Now(_clock);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var ev = new Event
        {
            Title = req.Title.Trim(),
            Description = req.Description,
            OwnerId = _me.UserId,
            RoomId = req.RoomId,
            StartAt = startAt,
            EndAt = endAt,
            IsAllDay = req.IsAllDay,
            RecurrenceRule = rrule,
            Status = EventStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _events.Add(ev);
        await _db.SaveChangesAsync(ct);

        foreach (var uid in attendeeIds)
            _db.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = uid });
        await _db.SaveChangesAsync(ct);

        await _booking.CreateOccurrencesAsync(ev, slots, ct);
        await _notifications.AddedToEventAsync(ev, slots[0].Start, attendeeIds, _me.DisplayName, ct);

        await tx.CommitAsync(ct);
        return ev.Id;
    }

    // ---------- 查詢 ----------

    public async Task<List<OccurrenceDto>> GetRangeAsync(DateTime from, DateTime to,
                                                          CalendarScope scope, int? roomId,
                                                          CancellationToken ct = default)
    {
        if (to <= from) throw new ValidationException("查詢區間的結束時間必須晚於開始時間");
        if ((to - from).TotalDays > 400) throw new ValidationException("查詢區間不可超過 400 天");

        var rows = scope switch
        {
            CalendarScope.Me => await _occurrences.GetRangeForUserAsync(_me.UserId, from, to, ct),
            CalendarScope.All => await _occurrences.GetRangeAllRoomsAsync(from, to, ct),
            CalendarScope.Room => await _occurrences.GetRangeForRoomAsync(
                roomId ?? throw new ValidationException("scope=room 必須指定 roomId"), from, to, ct),
            _ => throw new ValidationException("不支援的 scope"),
        };

        return rows.Select(ToOccurrenceDto).ToList();
    }

    public async Task<EventDetailDto> GetDetailAsync(int eventId, CancellationToken ct = default)
    {
        var ev = await _events.GetDetailAsync(eventId, ct) ?? throw new NotFoundException("找不到事件");

        var isAttendee = ev.Attendees.Any(a => a.UserId == _me.UserId);
        // 掛了會議廳的事件對所有已登入者可見（資源排程需要透明）；純個人事件僅擁有者與與會者可見。
        if (ev.OwnerId != _me.UserId && !isAttendee && ev.RoomId is null)
            throw new ForbiddenException("沒有權限查看此事件");

        return new EventDetailDto
        {
            Id = ev.Id,
            Title = ev.Title,
            Description = ev.Description,
            RoomId = ev.RoomId,
            RoomName = ev.Room?.Name,
            StartAt = ev.StartAt,
            EndAt = ev.EndAt,
            IsAllDay = ev.IsAllDay,
            Status = ev.Status.ToString(),
            OwnerId = ev.OwnerId,
            OwnerName = ev.Owner?.DisplayName ?? "",
            Recurrence = ev.RecurrenceRule is null ? null : _recurrence.ParseRrule(ev.RecurrenceRule),
            Attendees = ev.Attendees.Select(a => new AttendeeDto
            {
                UserId = a.UserId,
                DisplayName = a.User?.DisplayName ?? "",
                DepartmentName = a.User?.Department?.Name,
            }).OrderBy(a => a.DisplayName).ToList(),
            CanEdit = ev.OwnerId == _me.UserId || _me.IsAdmin,
        };
    }

    public async Task<List<AttendeeConflictDto>> CheckAttendeesAsync(AttendeeConflictRequest req,
                                                                     CancellationToken ct = default)
    {
        if (req.AttendeeIds.Count == 0 || req.Slots.Count == 0) return new();

        var from = req.Slots.Min(s => s.StartAt);
        var to = req.Slots.Max(s => s.EndAt);
        var ids = req.AttendeeIds.Distinct().ToList();

        var users = await _users.GetByIdsAsync(ids, ct);
        var rows = await _occurrences.GetRangeForUsersAsync(ids, from, to, ct);

        return users.Select(u =>
        {
            var hits = rows.Where(o =>
                    (o.Event!.OwnerId == u.Id || o.Event.Attendees.Any(a => a.UserId == u.Id))
                    && req.Slots.Any(s => OverlapChecker.Overlaps(s.StartAt, s.EndAt, o.StartAt, o.EndAt)))
                .ToList();

            return new AttendeeConflictDto
            {
                UserId = u.Id,
                DisplayName = u.DisplayName,
                ConflictCount = hits.Count,
                Titles = hits.Select(o => o.TitleOverride ?? o.Event!.Title).Distinct().ToList(),
            };
        }).ToList();
    }

    // ---------- 編輯 ----------

    public async Task UpdateAsync(int eventId, EditMode mode, UpdateEventRequest req,
                                  CancellationToken ct = default)
    {
        var ev = await _events.GetTrackedWithAttendeesAsync(eventId, ct)
                 ?? throw new NotFoundException("找不到事件");
        RequireEditPermission(ev);
        if (ev.Status == EventStatus.Cancelled) throw new ValidationException("已取消的事件不能編輯");

        if (mode == EditMode.Single) await UpdateSingleAsync(ev, req, ct);
        else await UpdateSeriesAsync(ev, req, ct);
    }

    private async Task UpdateSingleAsync(Event ev, UpdateEventRequest req, CancellationToken ct)
    {
        var occId = req.OccurrenceId
                    ?? throw new ValidationException("mode=single 必須指定 occurrenceId");

        var occ = await _occurrences.GetTrackedByIdAsync(occId, ct)
                  ?? throw new NotFoundException("找不到該次發生");
        if (occ.EventId != ev.Id) throw new ValidationException("該次發生不屬於此事件");
        if (occ.IsCancelled) throw new ValidationException("已取消的該次發生不能編輯");

        if (req.RoomId != ev.RoomId)
            throw new ValidationException("單筆編輯不可變更會議廳，請取消該次發生後另建事件");

        var (start, end) = Normalize(req.StartAt, req.EndAt, ev.IsAllDay);
        var timeChanged = start != occ.StartAt || end != occ.EndAt;

        var newTitle = req.Title.Trim() == ev.Title ? null : req.Title.Trim();
        var titleChanged = newTitle != occ.TitleOverride;

        if (!timeChanged && !titleChanged) return;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (timeChanged) await _booking.MoveOccurrenceAsync(occ, start, end, ct);
        if (titleChanged) await _booking.SetOccurrenceTitleAsync(occ, newTitle, ct);

        ev.UpdatedAt = TaipeiTime.Now(_clock);
        await _db.SaveChangesAsync(ct);

        // 僅修改標題不產生通知（規格 5.5）
        if (timeChanged)
            await _notifications.EventUpdatedAsync(ev, ev.Attendees.Select(a => a.UserId).ToList(),
                                                   occ.OriginalStartAt, start, null, ct);

        await tx.CommitAsync(ct);
    }

    private async Task UpdateSeriesAsync(Event ev, UpdateEventRequest req, CancellationToken ct)
    {
        var (start, end) = Normalize(req.StartAt, req.EndAt, req.IsAllDay);
        var rrule = BuildRrule(req.Recurrence, start);
        var slots = _recurrence.Expand(rrule, start, end);
        var attendeeIds = await ValidateAttendeesAsync(req.AttendeeIds, ct);

        var originalAttendees = ev.Attendees.Select(a => a.UserId).ToHashSet();
        var timeChanged = ev.StartAt != start || ev.EndAt != end || ev.RecurrenceRule != rrule;
        var roomChanged = ev.RoomId != req.RoomId;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        ev.Title = req.Title.Trim();
        ev.Description = req.Description;
        ev.RoomId = req.RoomId;
        ev.StartAt = start;
        ev.EndAt = end;
        ev.IsAllDay = req.IsAllDay;
        ev.RecurrenceRule = rrule;
        ev.UpdatedAt = TaipeiTime.Now(_clock);
        await _db.SaveChangesAsync(ct);

        await _booking.ReExpandSeriesAsync(ev, slots, ct);

        foreach (var gone in ev.Attendees.Where(a => !attendeeIds.Contains(a.UserId)).ToList())
            _db.EventAttendees.Remove(gone);
        foreach (var added in attendeeIds.Where(id => !originalAttendees.Contains(id)))
            _db.EventAttendees.Add(new EventAttendee { EventId = ev.Id, UserId = added });
        await _db.SaveChangesAsync(ct);

        if (timeChanged || roomChanged)
        {
            string? roomName = null;
            if (roomChanged && ev.RoomId is int rid)
                roomName = (await _rooms.GetByIdAsync(rid, ct))?.Name;

            var stillThere = attendeeIds.Where(id => originalAttendees.Contains(id)).ToList();
            await _notifications.EventUpdatedAsync(ev, stillThere, null, start, roomName, ct);
        }

        var newcomers = attendeeIds.Where(id => !originalAttendees.Contains(id)).ToList();
        if (newcomers.Count > 0)
            await _notifications.AddedToEventAsync(ev, slots[0].Start, newcomers, _me.DisplayName, ct);

        await tx.CommitAsync(ct);
    }

    // ---------- 取消 ----------

    public async Task CancelAsync(int eventId, EditMode mode, int? occurrenceId,
                                  CancellationToken ct = default)
    {
        var ev = await _events.GetTrackedWithAttendeesAsync(eventId, ct)
                 ?? throw new NotFoundException("找不到事件");
        RequireEditPermission(ev);

        var forced = _me.IsAdmin && ev.OwnerId != _me.UserId;
        var recipients = ev.Attendees.Select(a => a.UserId).ToList();
        if (forced) recipients.Add(ev.OwnerId);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        DateTime? occStart = null;
        if (mode == EditMode.Single)
        {
            var occId = occurrenceId
                        ?? throw new ValidationException("mode=single 必須指定 occurrenceId");
            var occ = await _occurrences.GetTrackedByIdAsync(occId, ct)
                      ?? throw new NotFoundException("找不到該次發生");
            if (occ.EventId != ev.Id) throw new ValidationException("該次發生不屬於此事件");

            occStart = occ.StartAt;
            await _booking.CancelOccurrenceAsync(occ, ct);
        }
        else
        {
            await _booking.CancelSeriesAsync(ev, ct);
        }

        ev.UpdatedAt = TaipeiTime.Now(_clock);
        await _db.SaveChangesAsync(ct);

        if (forced)
            await _notifications.ForcedCancellationAsync(ev, recipients, occStart, _me.DisplayName, ct);
        else
            await _notifications.EventCancelledAsync(ev, recipients, occStart, ct);

        await tx.CommitAsync(ct);
    }

    // ---------- 共用 ----------

    private void RequireEditPermission(Event ev)
    {
        if (ev.OwnerId != _me.UserId && !_me.IsAdmin)
            throw new ForbiddenException("只有事件擁有者或系統管理員可以修改此事件");
    }

    /// <summary>全天事件的時間部分固定為 00:00–23:59（規格 4.4）。</summary>
    private static (DateTime, DateTime) Normalize(DateTime start, DateTime end, bool isAllDay)
    {
        start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        end = DateTime.SpecifyKind(end, DateTimeKind.Unspecified);

        if (isAllDay)
        {
            start = start.Date;
            end = end.Date.AddHours(23).AddMinutes(59);
        }

        if (end <= start) throw new ValidationException("結束時間必須晚於開始時間");
        return (start, end);
    }

    private string? BuildRrule(RecurrencePatternDto? pattern, DateTime startAt)
    {
        if (pattern is null) return null;
        _recurrence.ValidateStartMatches(pattern, startAt);
        return _recurrence.ToRrule(pattern);
    }

    private async Task<List<int>> ValidateAttendeesAsync(List<int> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().Where(id => id != _me.UserId).ToList();
        if (distinct.Count == 0) return distinct;

        var found = await _users.GetByIdsAsync(distinct, ct);
        var inactive = found.Where(u => !u.IsActive).Select(u => u.DisplayName).ToList();

        if (found.Count != distinct.Count) throw new ValidationException("與會者名單中有不存在的使用者");
        if (inactive.Count > 0)
            throw new ValidationException($"與會者名單中有已停用的帳號：{string.Join("、", inactive)}");

        return distinct;
    }

    private OccurrenceDto ToOccurrenceDto(EventOccurrence o) => new()
    {
        OccurrenceId = o.Id,
        EventId = o.EventId,
        Title = o.TitleOverride ?? o.Event?.Title ?? "",
        StartAt = o.StartAt,
        EndAt = o.EndAt,
        IsAllDay = o.Event?.IsAllDay ?? false,
        RoomId = o.RoomId,
        RoomName = o.Room?.Name,
        OwnerId = o.Event?.OwnerId ?? 0,
        OwnerName = o.Event?.Owner?.DisplayName ?? "",
        IsRecurring = o.Event?.RecurrenceRule is not null,
        IsModified = o.IsModified,
        CanEdit = o.Event?.OwnerId == _me.UserId || _me.IsAdmin,
    };
}
