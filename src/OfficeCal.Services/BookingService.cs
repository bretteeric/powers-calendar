using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class BookingService : IBookingService
{
    private readonly OfficeCalDbContext _db;
    private readonly IRoomRepository _rooms;
    private readonly IEventOccurrenceRepository _occurrences;

    public BookingService(OfficeCalDbContext db, IRoomRepository rooms,
                          IEventOccurrenceRepository occurrences)
        => (_db, _rooms, _occurrences) = (db, rooms, occurrences);

    public async Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots,
                                             CancellationToken ct = default)
    {
        if (slots.Count == 0) throw new ValidationException("事件至少要有一次發生");

        if (ev.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);
            var conflicts = await FindConflictsAsync(roomId, slots, excludeEventId: ev.Id, ct);
            if (conflicts.Count > 0) throw new ConflictException("會議廳於下列時段已被預約", conflicts);
        }

        foreach (var s in slots)
        {
            _db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = ev.Id,
                OriginalStartAt = s.Start,
                StartAt = s.Start,
                EndAt = s.End,
                RoomId = ev.RoomId,
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelOccurrenceAsync(EventOccurrence occ, CancellationToken ct = default)
    {
        occ.IsCancelled = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelSeriesAsync(Event ev, CancellationToken ct = default)
    {
        ev.Status = EventStatus.Cancelled;
        var all = await _occurrences.GetTrackedByEventAsync(ev.Id, ct);
        foreach (var o in all) o.IsCancelled = true;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// 取得會議廳的寫入鎖。這是整個系統防止雙重預約的唯一機制：
    /// 所有寫入該會議廳 occurrence 的交易都會在這裡序列化，因此後續的衝突查詢
    /// 不需要額外加鎖也不會讀到別的交易正在寫入的資料。
    /// </summary>
    private async Task LockRoomForWriteAsync(int roomId, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "BookingService 必須在呼叫端已開啟的交易內執行，否則 UPDLOCK/HOLDLOCK 會立即釋放。");

        var room = await _rooms.LockAndGetAsync(roomId, ct)
                   ?? throw new NotFoundException($"會議廳不存在（Id={roomId}）");

        if (!room.IsActive)
            throw new ValidationException($"會議廳「{room.Name}」已停用，無法新增預約");
    }

    private async Task<List<ConflictDetail>> FindConflictsAsync(
        int roomId, IReadOnlyList<TimeSlot> slots, int? excludeEventId, CancellationToken ct)
    {
        var from = slots.Min(s => s.Start);
        var to = slots.Max(s => s.End);

        var existing = await _occurrences.GetRoomOccurrencesAsync(roomId, from, to, excludeEventId, ct);

        return existing
            .Where(e => slots.Any(s => OverlapChecker.Overlaps(s.Start, s.End, e.StartAt, e.EndAt)))
            .Select(e => new ConflictDetail
            {
                OccurrenceId = e.Id,
                RoomName = e.Room?.Name ?? "",
                StartAt = e.StartAt,
                EndAt = e.EndAt,
                OwnerName = e.Event?.Owner?.DisplayName ?? "",
                Title = e.TitleOverride ?? e.Event?.Title ?? "",
            })
            .OrderBy(c => c.StartAt)
            .ToList();
    }
}
