using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class RoomRepository : IRoomRepository
{
    private readonly OfficeCalDbContext _db;
    public RoomRepository(OfficeCalDbContext db) => _db = db;

    public Task<Room?> GetByIdAsync(int id, CancellationToken ct = default)
        => _db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<List<Room>> ListAsync(bool activeOnly, CancellationToken ct = default)
        => _db.Rooms.AsNoTracking()
                    .Where(r => !activeOnly || r.IsActive)
                    .OrderBy(r => r.Name)
                    .ToListAsync(ct);

    public async Task<Room?> LockAndGetAsync(int roomId, CancellationToken ct = default)
    {
        // 不可在此加上任何 LINQ 運算子：一旦組合，EF 會把這段 SQL 包成子查詢，
        // 資料表提示就未必落在正確的位置。ToListAsync 會原樣送出這段 SQL。
        var rows = await _db.Rooms
            .FromSqlInterpolated($"SELECT * FROM Rooms WITH (UPDLOCK, HOLDLOCK) WHERE Id = {roomId}")
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    public void Add(Room room) => _db.Rooms.Add(room);
}
