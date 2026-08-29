using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Infrastructure;

namespace OfficeCal.Tests.Fixtures;

public static class TestData
{
    public static async Task<User> AddUserAsync(OfficeCalDbContext db, string employeeNo,
                                                string name, UserRole role = UserRole.Employee)
    {
        var u = new User
        {
            EmployeeNo = employeeNo,
            DisplayName = name,
            Email = $"{employeeNo.ToLowerInvariant()}@corp.local",
            PasswordHash = "not-a-real-hash",
            Role = role,
            IcsFeedToken = Guid.NewGuid().ToString("N"),
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    public static async Task<Room> AddRoomAsync(OfficeCalDbContext db, string name,
                                                int capacity = 10, bool isActive = true)
    {
        var r = new Room { Name = name, Capacity = capacity, IsActive = isActive, Location = "A 棟 3F" };
        db.Rooms.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    /// <summary>建立一個已占用某會議廳的單次事件（直接寫庫，不經過 BookingService）。</summary>
    public static async Task<Event> AddBookedEventAsync(OfficeCalDbContext db, User owner, Room? room,
                                                        DateTime start, DateTime end,
                                                        string title = "既有會議",
                                                        bool cancelled = false)
    {
        var ev = new Event
        {
            Title = title,
            OwnerId = owner.Id,
            RoomId = room?.Id,
            StartAt = start,
            EndAt = end,
            Status = EventStatus.Active,
            CreatedAt = start,
            UpdatedAt = start,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        db.EventOccurrences.Add(new EventOccurrence
        {
            EventId = ev.Id,
            OriginalStartAt = start,
            StartAt = start,
            EndAt = end,
            RoomId = room?.Id,
            IsCancelled = cancelled,
        });
        await db.SaveChangesAsync();
        return ev;
    }
}
