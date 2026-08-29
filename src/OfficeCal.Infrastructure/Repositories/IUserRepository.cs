using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public interface IUserRepository
{
    Task<User?> GetByEmployeeNoAsync(string employeeNo, CancellationToken ct = default);
    Task<User?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<User?> GetByFeedTokenAsync(string token, CancellationToken ct = default);
    Task<List<User>> ListAsync(CancellationToken ct = default);
    Task<List<User>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default);
    void Add(User user);
}
