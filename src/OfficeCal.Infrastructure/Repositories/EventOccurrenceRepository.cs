using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;

namespace OfficeCal.Infrastructure.Repositories;

public class EventOccurrenceRepository : IEventOccurrenceRepository
{
    private readonly OfficeCalDbContext _db;
    public EventOccurrenceRepository(OfficeCalDbContext db) => _db = db;

    private IQueryable<EventOccurrence> WithDetails()
        => _db.EventOccurrences.AsNoTracking()
              .Include(o => o.Event!).ThenInclude(e => e.Owner)
              .Include(o => o.Room);

    public Task<List<EventOccurrence>> GetRoomOccurrencesAsync(
        int roomId, DateTime from, DateTime to, int? excludeEventId, int? excludeOccurrenceId,
        CancellationToken ct = default)
        => WithDetails()
            .Where(o => o.RoomId == roomId && !o.IsCancelled
                        && o.StartAt < to && o.EndAt > from
                        && (excludeEventId == null || o.EventId != excludeEventId)
                        && (excludeOccurrenceId == null || o.Id != excludeOccurrenceId))
            .ToListAsync(ct);

    public Task<List<EventOccurrence>> GetRangeForUserAsync(
        int userId, DateTime from, DateTime to, CancellationToken ct = default)
        => WithDetails()
            .Where(o => !o.IsCancelled && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active
                        && (o.Event.OwnerId == userId
                            || o.Event.Attendees.Any(a => a.UserId == userId)))
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

    public Task<List<EventOccurrence>> GetRangeForRoomAsync(
        int roomId, DateTime from, DateTime to, CancellationToken ct = default)
        => WithDetails()
            .Where(o => !o.IsCancelled && o.RoomId == roomId && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active)
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

    public Task<List<EventOccurrence>> GetRangeAllRoomsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
        => WithDetails()
            .Where(o => !o.IsCancelled && o.RoomId != null && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active)
            .OrderBy(o => o.StartAt)
            .ToListAsync(ct);

    // 這個查詢刻意不用 WithDetails()：呼叫端（CheckAttendeesAsync）要在記憶體裡
    // 判斷每一筆是「誰的」行程，因此必須連 Attendees 一起載入。
    // 少了這個 Include，AsNoTracking 查詢回來的 Attendees 會是空清單，
    // 「被邀請」而忙碌的與會者就會被算成 0 次衝突。
    public Task<List<EventOccurrence>> GetRangeForUsersAsync(
        IReadOnlyCollection<int> userIds, DateTime from, DateTime to, CancellationToken ct = default)
        => _db.EventOccurrences.AsNoTracking()
            .Include(o => o.Event!).ThenInclude(e => e.Owner)
            .Include(o => o.Event!).ThenInclude(e => e.Attendees)
            .Include(o => o.Room)
            .Where(o => !o.IsCancelled && o.StartAt < to && o.EndAt > from
                        && o.Event!.Status == EventStatus.Active
                        && (userIds.Contains(o.Event.OwnerId)
                            || o.Event.Attendees.Any(a => userIds.Contains(a.UserId))))
            .ToListAsync(ct);

    public Task<EventOccurrence?> GetTrackedByIdAsync(int occurrenceId, CancellationToken ct = default)
        => _db.EventOccurrences
              .Include(o => o.Event)
              .FirstOrDefaultAsync(o => o.Id == occurrenceId, ct);

    public Task<List<EventOccurrence>> GetTrackedByEventAsync(int eventId, CancellationToken ct = default)
        => _db.EventOccurrences.Where(o => o.EventId == eventId)
                               .OrderBy(o => o.OriginalStartAt)
                               .ToListAsync(ct);
}
