using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IEventRepository
{
    void Add(Event ev);
    /// <summary>受追蹤，含 Attendees，供編輯使用。</summary>
    Task<Event?> GetTrackedWithAttendeesAsync(int id, CancellationToken ct = default);
    /// <summary>唯讀，含 Owner、Room、Attendees.User，供明細使用。</summary>
    Task<Event?> GetDetailAsync(int id, CancellationToken ct = default);
}
