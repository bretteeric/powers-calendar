using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class NotificationService : INotificationService
{
    private readonly OfficeCalDbContext _db;
    private readonly INotificationRepository _repo;
    private readonly TimeProvider _clock;

    public NotificationService(OfficeCalDbContext db, INotificationRepository repo, TimeProvider clock)
        => (_db, _repo, _clock) = (db, repo, clock);

    /// <summary>訊息中的日期時間格式，例如 9/14 14:00。</summary>
    private static string F(DateTime d) => $"{d.Month}/{d.Day} {d:HH:mm}";
    private static string D(DateTime d) => $"{d.Month}/{d.Day}";

    public Task AddedToEventAsync(Event ev, DateTime firstStart, IReadOnlyCollection<int> userIds,
                                  string ownerName, CancellationToken ct = default)
        => WriteAsync(userIds.Where(id => id != ev.OwnerId), NotificationType.AddedToEvent, ev.Id,
                      $"{ownerName} 邀請你參加 {F(firstStart)} 的「{ev.Title}」", ct);

    public Task EventUpdatedAsync(Event ev, IReadOnlyCollection<int> userIds,
                                  DateTime? occurrenceOriginalStart, DateTime newStart,
                                  string? roomName, CancellationToken ct = default)
    {
        var prefix = occurrenceOriginalStart is DateTime o ? $"{D(o)} 的" : "";
        var message = roomName is null
            ? $"{prefix}「{ev.Title}」已改期至 {F(newStart)}"
            : $"{prefix}「{ev.Title}」已改至 {F(newStart)}，會議廳改為「{roomName}」";
        return WriteAsync(userIds, NotificationType.EventUpdated, ev.Id, message, ct);
    }

    public Task EventCancelledAsync(Event ev, IReadOnlyCollection<int> userIds,
                                    DateTime? occurrenceStart, CancellationToken ct = default)
    {
        var message = occurrenceStart is DateTime o
            ? $"{D(o)} 的「{ev.Title}」已取消"
            : $"「{ev.Title}」整個系列已取消";
        return WriteAsync(userIds, NotificationType.EventCancelled, ev.Id, message, ct);
    }

    public Task ForcedCancellationAsync(Event ev, IReadOnlyCollection<int> userIds,
                                        DateTime? occurrenceStart, string adminName,
                                        CancellationToken ct = default)
    {
        var what = occurrenceStart is DateTime o ? $"{F(o)} 的「{ev.Title}」" : $"「{ev.Title}」整個系列";
        return WriteAsync(userIds, NotificationType.ForcedCancellation, ev.Id,
                          $"{adminName} 已強制取消 {what}，該時段的會議廳已釋出", ct);
    }

    private async Task WriteAsync(IEnumerable<int> userIds, NotificationType type, int? eventId,
                                  string message, CancellationToken ct)
    {
        var now = TaipeiTime.Now(_clock);
        var rows = userIds.Distinct()
            .Select(id => new Notification
            {
                UserId = id,
                Type = type,
                EventId = eventId,
                Message = message.Length > 300 ? message[..300] : message,
                IsRead = false,
                CreatedAt = now,
            })
            .ToList();

        if (rows.Count == 0) return;

        _repo.AddRange(rows);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<NotificationDto>> ListAsync(int userId, bool unreadOnly, int take,
                                                       CancellationToken ct = default)
        => (await _repo.ListAsync(userId, unreadOnly, take, ct))
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type.ToString(),
                EventId = n.EventId,
                Message = n.Message,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt,
            })
            .ToList();

    public Task<int> UnreadCountAsync(int userId, CancellationToken ct = default)
        => _repo.UnreadCountAsync(userId, ct);

    public async Task MarkReadAsync(int notificationId, int userId, CancellationToken ct = default)
    {
        var n = await _repo.GetTrackedByIdAsync(notificationId, ct)
                ?? throw new NotFoundException("找不到通知");

        if (n.UserId != userId) throw new ForbiddenException("只能標記自己的通知");

        n.IsRead = true;
        await _db.SaveChangesAsync(ct);
    }
}
