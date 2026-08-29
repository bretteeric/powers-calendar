using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly OfficeCalDbContext _db;
    public UserRepository(OfficeCalDbContext db) => _db = db;

    public Task<User?> GetByEmployeeNoAsync(string employeeNo, CancellationToken ct = default)
        => _db.Users.FirstOrDefaultAsync(u => u.EmployeeNo == employeeNo, ct);

    public Task<User?> GetByIdAsync(int id, CancellationToken ct = default)
        => _db.Users.Include(u => u.Department).FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByFeedTokenAsync(string token, CancellationToken ct = default)
        => _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.IcsFeedToken == token && u.IsActive, ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default)
        => _db.Users.AsNoTracking().Include(u => u.Department)
                    .OrderBy(u => u.EmployeeNo).ToListAsync(ct);

    public Task<List<User>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
        => _db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);

    public void Add(User user) => _db.Users.Add(user);
}
