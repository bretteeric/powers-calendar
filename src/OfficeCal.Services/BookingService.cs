using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
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
    private readonly TimeProvider _clock;

    public BookingService(OfficeCalDbContext db, IRoomRepository rooms,
                          IEventOccurrenceRepository occurrences, TimeProvider clock)
        => (_db, _rooms, _occurrences, _clock) = (db, rooms, occurrences, clock);

    public async Task CreateOccurrencesAsync(Event ev, IReadOnlyList<TimeSlot> slots,
                                             CancellationToken ct = default)
    {
        if (slots.Count == 0) throw new ValidationException("事件至少要有一次發生");

        if (ev.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);
            EnsureNoSelfOverlap(slots);
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

    public async Task ReExpandSeriesAsync(Event ev, IReadOnlyList<TimeSlot> slots,
                                          CancellationToken ct = default)
    {
        var now = TaipeiTime.Now(_clock);
        var existing = await _occurrences.GetTrackedByEventAsync(ev.Id, ct);

        // 保留：已發生過的（不回頭改寫歷史）、被單獨修改的、被單獨取消的
        var survivors = existing
            .Where(o => o.StartAt <= now || o.IsModified || o.IsCancelled)
            .ToList();
        var toDelete = existing.Except(survivors).ToList();

        // 去重時要比對「全部」保留列的 OriginalStartAt，不只是被修改／取消的那些：
        // 已發生過的那幾次同樣是一次發生，不可以再長出第二列。
        //
        // 比對的粒度是「日曆日」而非精確時間。OriginalStartAt 記錄的是這次發生原本屬於系列的
        // 哪個位置，單筆編輯（MoveOccurrenceAsync）從不更新它；一旦整系列編輯改動了時間
        // （例如 10:00 → 11:00），新 slot 的 Start 與保留列的 OriginalStartAt 就注定不相等，
        // 精確比對必然落空，會讓同一天長出第二列、或讓被單獨取消的那次以新時間復活。
        // 本系統的重複頻率只有 Daily／Weekly／Monthly／Yearly，沒有日內頻率，因此一個系列在
        // 每個日曆日至多產生一次 occurrence——日曆日是系列內有效且唯一的實例身分。
        var keptOriginalDates = survivors.Select(o => o.OriginalStartAt.Date).ToHashSet();

        var newSlots = slots
            .Where(s => s.Start > now && !keptOriginalDates.Contains(s.Start.Date))
            .ToList();

        // 系列換會議廳時，保留下來的未來 occurrence 也要搬過去（見任務 7 假設 1）
        var movedSurvivors = survivors
            .Where(o => o.StartAt > now && !o.IsCancelled && o.RoomId != ev.RoomId)
            .ToList();

        if (ev.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);

            var toCheck = newSlots
                .Concat(movedSurvivors.Select(o => new TimeSlot(o.StartAt, o.EndAt)))
                .ToList();

            EnsureNoSelfOverlap(toCheck);

            if (toCheck.Count > 0)
            {
                var conflicts = await FindConflictsAsync(roomId, toCheck, excludeEventId: ev.Id, ct);
                if (conflicts.Count > 0)
                    throw new ConflictException("會議廳於下列時段已被預約", conflicts);
            }
        }

        _db.EventOccurrences.RemoveRange(toDelete);
        foreach (var o in movedSurvivors) o.RoomId = ev.RoomId;

        foreach (var s in newSlots)
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

    public async Task MoveOccurrenceAsync(EventOccurrence occ, DateTime newStart, DateTime newEnd,
                                          CancellationToken ct = default)
    {
        if (newEnd <= newStart) throw new ValidationException("結束時間必須晚於開始時間");

        if (occ.RoomId is int roomId)
        {
            await LockRoomForWriteAsync(roomId, ct);
            var conflicts = await FindConflictsAsync(
                roomId, new[] { new TimeSlot(newStart, newEnd) }, excludeEventId: occ.EventId, ct);
            if (conflicts.Count > 0) throw new ConflictException("會議廳於下列時段已被預約", conflicts);
        }

        occ.StartAt = newStart;
        occ.EndAt = newEnd;
        occ.IsModified = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetOccurrenceTitleAsync(EventOccurrence occ, string? title,
                                              CancellationToken ct = default)
    {
        occ.TitleOverride = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        occ.IsModified = true;
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

    /// <summary>
    /// 檢查同一批 slots 之間是否互相重疊。重複規則的長度若超過間隔（例如每日重複但長度 26 小時），
    /// 展開結果會自我重疊；這批 slot 從未彼此比對過，會全部寫進同一間會議廳。
    /// </summary>
    private static void EnsureNoSelfOverlap(IReadOnlyList<TimeSlot> slots)
    {
        var ordered = slots.OrderBy(s => s.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
            if (OverlapChecker.Overlaps(ordered[i - 1].Start, ordered[i - 1].End,
                                        ordered[i].Start, ordered[i].End))
                throw new ValidationException(
                    "重複規則產生了互相重疊的時段，請縮短事件長度或加大重複間隔");
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
