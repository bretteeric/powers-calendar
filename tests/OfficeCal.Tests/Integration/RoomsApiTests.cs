using System.Net;
using System.Net.Http.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class RoomsApiTests
{
    private readonly ApiFactory _api;
    public RoomsApiTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task 已登入者可取得會議廳清單()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var res = await client.GetFromJsonAsync<ApiResponse<List<RoomDto>>>("/api/v1/rooms",
                                                                            ApiFactory.Json);
        Assert.True(res!.Data!.Count >= 3);   // 種子資料有三間
    }

    [Fact]
    public async Task 非管理員不能維護會議廳()
    {
        await _api.EnsureEmployeeAsync("E200", "王小明");
        var employee = await _api.LoginAsync("E200", ApiFactory.EmployeePassword);

        var res = await employee.PostAsJsonAsync("/api/v1/rooms",
            new RoomRequest { Name = "偷偷新增的會議室", Capacity = 5 });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 管理員可新增會議廳且名稱重複回四百()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var ok = await admin.PostAsJsonAsync("/api/v1/rooms",
            new RoomRequest { Name = "C 棟 5F 訓練教室", Location = "C 棟 5 樓", Capacity = 30 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var dup = await admin.PostAsJsonAsync("/api/v1/rooms",
            new RoomRequest { Name = "C 棟 5F 訓練教室", Capacity = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);
    }

    [Fact]
    public async Task 空房查詢回傳當日占用時段並可依人數過濾()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var rooms = (await admin.GetFromJsonAsync<ApiResponse<List<RoomDto>>>("/api/v1/rooms",
                                                                              ApiFactory.Json))!.Data!;
        var big = rooms.OrderByDescending(r => r.Capacity).First();

        var day = new DateTime(2027, 5, 10, 0, 0, 0);
        await admin.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "占用測試", RoomId = big.Id,
            StartAt = day.AddHours(10), EndAt = day.AddHours(11),
        });

        var res = await admin.GetFromJsonAsync<ApiResponse<List<RoomAvailabilityDto>>>(
            $"/api/v1/rooms/availability?date=2027-05-10&capacity={big.Capacity}", ApiFactory.Json);

        var row = res!.Data!.Single(r => r.RoomId == big.Id);
        Assert.Contains(row.Busy, b => b.StartAt == day.AddHours(10) && b.Title == "占用測試");
        Assert.All(res.Data!, r => Assert.True(r.Capacity >= big.Capacity));
    }

    // --- 最終審查重要 4：授權屬性是「刪掉不會有任何東西轉紅」的那類程式碼。
    // 任務 14 對 users 的三個端點做過同樣的補齊，rooms 的 PUT 被漏下了。 ---

    [Fact]
    public async Task 非管理員不能編輯會議廳()
    {
        await _api.EnsureEmployeeAsync("E201", "李小華");
        var employee = await _api.LoginAsync("E201", ApiFactory.EmployeePassword);

        // 讀取會議廳清單本來就開放給所有已登入者，拿得到 id 不代表可以改
        var rooms = (await employee.GetFromJsonAsync<ApiResponse<List<RoomDto>>>(
            "/api/v1/rooms", ApiFactory.Json))!.Data!;
        var target = rooms[0];

        var res = await employee.PutAsJsonAsync($"/api/v1/rooms/{target.Id}",
            new RoomRequest { Name = "偷偷改名的會議室", Capacity = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        // 名稱與容量都沒有被改動
        var after = (await employee.GetFromJsonAsync<ApiResponse<List<RoomDto>>>(
            "/api/v1/rooms", ApiFactory.Json))!.Data!.Single(r => r.Id == target.Id);
        Assert.Equal(target.Name, after.Name);
        Assert.Equal(target.Capacity, after.Capacity);
    }
}
