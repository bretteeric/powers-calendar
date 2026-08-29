using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class EventsApiTests
{
    private readonly ApiFactory _api;
    public EventsApiTests(ApiFactory api) => _api = api;

    private static DateTime D(int day, int hour) => new(2027, 3, day, hour, 0, 0);

    private static CreateEventRequest Req(string title, int? roomId, int day, int hour) => new()
    {
        Title = title, RoomId = roomId, StartAt = D(day, hour), EndAt = D(day, hour + 1),
    };

    /// <summary>直接查資料庫取種子會議廳，讓本測試不相依於任務 12 的會議廳 API。</summary>
    private async Task<int> FirstRoomIdAsync()
    {
        await using var db = _api.CreateContext();
        return await db.Rooms.OrderBy(r => r.Id).Select(r => r.Id).FirstAsync();
    }

    [Fact]
    public async Task 建立事件並在區間查詢中看到它()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var create = await client.PostAsJsonAsync("/api/v1/events", Req("驗收會議", null, 2, 9));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json);
        Assert.True(created!.Data > 0);

        var list = await client.GetFromJsonAsync<ApiResponse<List<OccurrenceDto>>>(
            $"/api/v1/events?from={D(2, 0):s}&to={D(3, 0):s}&scope=me", ApiFactory.Json);
        Assert.Contains(list!.Data!, o => o.Title == "驗收會議");
    }

    [Fact]
    public async Task 重複預約同一會議廳的重疊時段回四百零九並附衝突明細()
    {
        var roomId = await FirstRoomIdAsync();
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var first = await client.PostAsJsonAsync("/api/v1/events", Req("先到先得", roomId, 5, 14));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/events", Req("後到被擋", roomId, 5, 14));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        var conflicts = root.GetProperty("data").GetProperty("conflicts");
        Assert.Equal(1, conflicts.GetArrayLength());
        Assert.Equal("先到先得", conflicts[0].GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(conflicts[0].GetProperty("roomName").GetString()));
    }

    [Fact]
    public async Task 一般員工不能修改他人事件()
    {
        await _api.EnsureEmployeeAsync("E100", "王小明");
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var create = await admin.PostAsJsonAsync("/api/v1/events", Req("管理員的事件", null, 8, 10));
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;

        var employee = await _api.LoginAsync("E100", ApiFactory.EmployeePassword);
        var res = await employee.PutAsJsonAsync($"/api/v1/events/{id}?mode=series",
            new UpdateEventRequest { Title = "被亂改", StartAt = D(8, 10), EndAt = D(8, 11) });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 管理員可以強制取消他人預約()
    {
        var employeeId = await _api.EnsureEmployeeAsync("E101", "李小華");
        var roomId = await FirstRoomIdAsync();

        var employee = await _api.LoginAsync("E101", ApiFactory.EmployeePassword);
        var create = await employee.PostAsJsonAsync("/api/v1/events", Req("員工的預約", roomId, 9, 10));
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;

        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var del = await admin.DeleteAsync($"/api/v1/events/{id}?mode=series");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        // 擁有者收到強制取消通知
        var notes = await employee.GetFromJsonAsync<ApiResponse<NotificationListDto>>(
            "/api/v1/notifications?unreadOnly=true", ApiFactory.Json);
        Assert.Contains(notes!.Data!.Items, n => n.Type == "ForcedCancellation");
    }

    [Fact]
    public async Task Scope為room卻未附roomId回四百()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var res = await client.GetAsync($"/api/v1/events?from={D(2, 0):s}&to={D(3, 0):s}&scope=room");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task 未登入不能建立事件()
    {
        var client = _api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing
            .WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.PostAsJsonAsync("/api/v1/events", Req("匿名事件", null, 2, 9));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
