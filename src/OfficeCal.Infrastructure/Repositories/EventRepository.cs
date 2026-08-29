using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;

namespace OfficeCal.Infrastructure.Repositories;

public class EventRepository : IEventRepository
{
    private readonly OfficeCalDbContext _db;
    public EventRepository(OfficeCalDbContext db) => _db = db;

    public void Add(Event ev) => _db.Events.Add(ev);

    public Task<Event?> GetTrackedWithAttendeesAsync(int id, CancellationToken ct = default)
        => _db.Events.Include(e => e.Attendees).FirstOrDefaultAsync(e => e.Id == id, ct);

    public Task<Event?> GetDetailAsync(int id, CancellationToken ct = default)
        => _db.Events.AsNoTracking()
              .Include(e => e.Owner)
              .Include(e => e.Room)
              .Include(e => e.Attendees).ThenInclude(a => a.User!).ThenInclude(u => u.Department)
              .FirstOrDefaultAsync(e => e.Id == id, ct);
}
