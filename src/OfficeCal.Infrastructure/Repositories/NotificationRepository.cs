using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly OfficeCalDbContext _db;
    public NotificationRepository(OfficeCalDbContext db) => _db = db;

    public void AddRange(IEnumerable<Notification> notifications)
        => _db.Notifications.AddRange(notifications);

    public Task<List<Notification>> ListAsync(int userId, bool unreadOnly, int take,
                                              CancellationToken ct = default)
        => _db.Notifications.AsNoTracking()
              .Where(n => n.UserId == userId && (!unreadOnly || !n.IsRead))
              .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id)
              .Take(take)
              .ToListAsync(ct);

    public Task<int> UnreadCountAsync(int userId, CancellationToken ct = default)
        => _db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct);

    public Task<Notification?> GetTrackedByIdAsync(int id, CancellationToken ct = default)
        => _db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
}
