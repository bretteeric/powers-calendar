using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class SchemaTests
{
    private readonly LocalDbFixture _db;
    public SchemaTests(LocalDbFixture db) => _db = db;

    [Fact]
    public async Task 同一事件的同一次發生不能有兩列()
    {
        await _db.ResetAsync();
        await using var ctx = _db.CreateContext();

        var owner = new User
        {
            EmployeeNo = "E001", DisplayName = "測試員", Email = "e001@corp.local",
            PasswordHash = "x", IcsFeedToken = Guid.NewGuid().ToString("N"),
        };
        ctx.Users.Add(owner);
        await ctx.SaveChangesAsync();

        var ev = new Event
        {
            Title = "測試事件", OwnerId = owner.Id,
            StartAt = new DateTime(2026, 9, 7, 10, 0, 0),
            EndAt = new DateTime(2026, 9, 7, 11, 0, 0),
        };
        ctx.Events.Add(ev);
        await ctx.SaveChangesAsync();

        ctx.EventOccurrences.Add(new EventOccurrence
        {
            EventId = ev.Id,
            OriginalStartAt = new DateTime(2026, 9, 7, 10, 0, 0),
            StartAt = new DateTime(2026, 9, 7, 10, 0, 0),
            EndAt = new DateTime(2026, 9, 7, 11, 0, 0),
        });
        await ctx.SaveChangesAsync();

        ctx.EventOccurrences.Add(new EventOccurrence
        {
            EventId = ev.Id,
            OriginalStartAt = new DateTime(2026, 9, 7, 10, 0, 0),   // 同一次發生
            StartAt = new DateTime(2026, 9, 7, 14, 0, 0),
            EndAt = new DateTime(2026, 9, 7, 15, 0, 0),
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task 衝突偵測用的篩選索引存在且帶有正確的篩選條件()
    {
        await using var ctx = _db.CreateContext();
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT filter_definition FROM sys.indexes WHERE name = 'IX_EventOccurrences_Room_Range'";
        var filter = (string?)await cmd.ExecuteScalarAsync();

        Assert.NotNull(filter);
        Assert.Contains("IsCancelled", filter);
        Assert.Contains("RoomId", filter);
    }
}
