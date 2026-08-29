using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("LocalDb")]
public class ConcurrentBookingTests
{
    private readonly LocalDbFixture _db;
    public ConcurrentBookingTests(LocalDbFixture db) => _db = db;

    /// <summary>
    /// 一次完整的預約嘗試：自己的 DbContext（＝自己的連線與交易）。
    /// 回傳 true 表示成功，false 表示收到 409 衝突。其他例外一律往外丟。
    /// </summary>
    private async Task<bool> TryBookAsync(int ownerId, int roomId, TimeSlot slot, Task gate)
    {
        await using var ctx = _db.CreateContext();
        var booking = new BookingService(ctx, new RoomRepository(ctx),
                                         new EventOccurrenceRepository(ctx), TimeProvider.System);
        await gate;   // 兩個執行緒在此對齊，盡可能同時衝進交易

        await using var tx = await ctx.Database.BeginTransactionAsync();
        try
        {
            var ev = new Event
            {
                Title = "併發測試", OwnerId = ownerId, RoomId = roomId,
                StartAt = slot.Start, EndAt = slot.End,
                CreatedAt = slot.Start, UpdatedAt = slot.Start,
            };
            ctx.Events.Add(ev);
            await ctx.SaveChangesAsync();

            await booking.CreateOccurrencesAsync(ev, new[] { slot });
            await tx.CommitAsync();
            return true;
        }
        catch (ConflictException)
        {
            await tx.RollbackAsync();
            return false;
        }
    }

    [Fact]
    public async Task 同一會議廳同一時段連續五十輪都恰好一個成功()
    {
        await _db.ResetAsync();
        int ownerId, roomId;
        await using (var seed = _db.CreateContext())
        {
            ownerId = (await TestData.AddUserAsync(seed, "E001", "陳大明")).Id;
            roomId = (await TestData.AddRoomAsync(seed, "A 棟 3F 大會議廳")).Id;
        }

        for (var round = 0; round < 50; round++)
        {
            var start = new DateTime(2026, 9, 1, 10, 0, 0).AddDays(round);
            var slot = new TimeSlot(start, start.AddHours(1));

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = Task.Run(() => TryBookAsync(ownerId, roomId, slot, gate.Task));
            var b = Task.Run(() => TryBookAsync(ownerId, roomId, slot, gate.Task));
            gate.SetResult();

            var results = await Task.WhenAll(a, b);

            Assert.True(results.Count(x => x) == 1,
                $"第 {round} 輪應恰好一個成功，實際成功 {results.Count(x => x)} 個");

            await using var verify = _db.CreateContext();
            var written = await verify.EventOccurrences
                .CountAsync(o => o.RoomId == roomId && o.StartAt == slot.Start && !o.IsCancelled);
            Assert.Equal(1, written);
        }
    }

    [Fact]
    public async Task 不同會議廳的相同時段兩者都成功()
    {
        await _db.ResetAsync();
        int ownerId, roomA, roomB;
        await using (var seed = _db.CreateContext())
        {
            ownerId = (await TestData.AddUserAsync(seed, "E001", "陳大明")).Id;
            roomA = (await TestData.AddRoomAsync(seed, "A 棟 3F 大會議廳")).Id;
            roomB = (await TestData.AddRoomAsync(seed, "B 棟 2F 小會議室")).Id;
        }

        var start = new DateTime(2026, 9, 7, 10, 0, 0);
        var slot = new TimeSlot(start, start.AddHours(1));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(() => TryBookAsync(ownerId, roomA, slot, gate.Task));
        var b = Task.Run(() => TryBookAsync(ownerId, roomB, slot, gate.Task));
        gate.SetResult();

        var results = await Task.WhenAll(a, b);
        Assert.Equal(2, results.Count(x => x));   // 鎖的粒度是單一會議廳，不同會議廳互不阻塞
    }
}
