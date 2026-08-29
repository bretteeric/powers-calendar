using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface INotificationRepository
{
    void AddRange(IEnumerable<Notification> notifications);
    Task<List<Notification>> ListAsync(int userId, bool unreadOnly, int take, CancellationToken ct = default);
    Task<int> UnreadCountAsync(int userId, CancellationToken ct = default);
    Task<Notification?> GetTrackedByIdAsync(int id, CancellationToken ct = default);
}
