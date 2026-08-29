using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class RoomService : IRoomService
{
    private readonly OfficeCalDbContext _db;
    private readonly IRoomRepository _rooms;
    private readonly IEventOccurrenceRepository _occurrences;

    public RoomService(OfficeCalDbContext db, IRoomRepository rooms,
                       IEventOccurrenceRepository occurrences)
        => (_db, _rooms, _occurrences) = (db, rooms, occurrences);

    public async Task<List<RoomDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
        => (await _rooms.ListAsync(activeOnly, ct)).Select(ToDto).ToList();

    public async Task<List<RoomAvailabilityDto>> GetAvailabilityAsync(DateOnly date, int? minCapacity,
                                                                       CancellationToken ct = default)
    {
        var from = date.ToDateTime(TimeOnly.MinValue);
        var to = from.AddDays(1);

        var rooms = (await _rooms.ListAsync(activeOnly: true, ct))
            .Where(r => minCapacity is null || r.Capacity >= minCapacity)
            .ToList();

        var result = new List<RoomAvailabilityDto>(rooms.Count);
        foreach (var room in rooms)
        {
            var busy = await _occurrences.GetRangeForRoomAsync(room.Id, from, to, ct);
            result.Add(new RoomAvailabilityDto
            {
                RoomId = room.Id,
                Name = room.Name,
                Location = room.Location,
                Capacity = room.Capacity,
                Equipment = room.Equipment,
                Busy = busy.Select(o => new BusySlotDto
                {
                    OccurrenceId = o.Id,
                    EventId = o.EventId,
                    StartAt = o.StartAt,
                    EndAt = o.EndAt,
                    Title = o.TitleOverride ?? o.Event?.Title ?? "",
                    OwnerName = o.Event?.Owner?.DisplayName ?? "",
                }).OrderBy(b => b.StartAt).ToList(),
            });
        }
        return result;
    }

    public async Task<int> CreateAsync(RoomRequest req, CancellationToken ct = default)
    {
        var name = req.Name.Trim();
        if (await _db.Rooms.AnyAsync(r => r.Name == name, ct))
            throw new ValidationException($"已經有名稱為「{name}」的會議廳");

        var room = new Room
        {
            Name = name, Location = req.Location, Capacity = req.Capacity,
            Equipment = req.Equipment, IsActive = req.IsActive,
        };
        _rooms.Add(room);
        await _db.SaveChangesAsync(ct);
        return room.Id;
    }

    public async Task UpdateAsync(int id, RoomRequest req, CancellationToken ct = default)
    {
        var room = await _rooms.GetByIdAsync(id, ct) ?? throw new NotFoundException("找不到會議廳");
        var name = req.Name.Trim();

        if (await _db.Rooms.AnyAsync(r => r.Name == name && r.Id != id, ct))
            throw new ValidationException($"已經有名稱為「{name}」的會議廳");

        room.Name = name;
        room.Location = req.Location;
        room.Capacity = req.Capacity;
        room.Equipment = req.Equipment;
        room.IsActive = req.IsActive;   // 停用後不可新增預約，既有預約保留
        await _db.SaveChangesAsync(ct);
    }

    private static RoomDto ToDto(Room r) => new()
    {
        Id = r.Id, Name = r.Name, Location = r.Location,
        Capacity = r.Capacity, Equipment = r.Equipment, IsActive = r.IsActive,
    };
}
