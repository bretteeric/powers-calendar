using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class IcsApiTests
{
    private readonly ApiFactory _api;
    public IcsApiTests(ApiFactory api) => _api = api;

    private static DateTime D(int day, int hour) => new(2027, 7, day, hour, 0, 0);

    [Fact]
    public async Task 單筆匯出含台北時區區塊與正確時間()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var create = await client.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "半導體; 研討會, 說明",   // 刻意帶需要跳脫的字元
            StartAt = D(5, 10), EndAt = D(5, 11),
        });
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;

        var res = await client.GetAsync($"/api/v1/events/{id}/ics");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType!.MediaType);

        var ics = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR", ics);
        Assert.Contains("BEGIN:VTIMEZONE", ics);
        Assert.Contains("TZID:Asia/Taipei", ics);
        Assert.Contains("DTSTART;TZID=Asia/Taipei:20270705T100000", ics);
        Assert.Contains("DTEND;TZID=Asia/Taipei:20270705T110000", ics);
        Assert.Contains(@"半導體\; 研討會\, 說明", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public async Task 訂閱feed為匿名端點且以token授權()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        await client.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "訂閱測試會議", StartAt = D(6, 9), EndAt = D(6, 10),
        });

        var me = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var path = new Uri(me!.Data!.FeedUrl).AbsolutePath;

        // 不帶 Cookie 的全新 client
        var anonymous = _api.CreateClient();
        var res = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var ics = await res.Content.ReadAsStringAsync();
        Assert.Contains("訂閱測試會議", ics);
        Assert.Contains("UID:", ics);
        Assert.Contains("@calendar.local", ics);
    }

    [Fact]
    public async Task 錯誤的feedtoken回四百零四()
    {
        var anonymous = _api.CreateClient();
        var res = await anonymous.GetAsync("/feeds/this-token-does-not-exist.ics");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task 已取消的發生不出現在feed中()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var create = await client.PostAsJsonAsync("/api/v1/events", new CreateEventRequest
        {
            Title = "會被取消的會議", StartAt = D(7, 9), EndAt = D(7, 10),
        });
        var id = (await create.Content.ReadFromJsonAsync<ApiResponse<int>>(ApiFactory.Json))!.Data;
        await client.DeleteAsync($"/api/v1/events/{id}?mode=series");

        var me = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var ics = await _api.CreateClient().GetStringAsync(new Uri(me!.Data!.FeedUrl).AbsolutePath);

        Assert.DoesNotContain("會被取消的會議", ics);
    }

    [Fact]
    public async Task 重新產生token後舊網址失效()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var before = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var oldPath = new Uri(before!.Data!.FeedUrl).AbsolutePath;

        var reset = await client.PostAsync("/api/v1/me/reset-feed-token", null);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var anonymous = _api.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(oldPath)).StatusCode);

        var after = await client.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        var newPath = new Uri(after!.Data!.FeedUrl).AbsolutePath;
        Assert.NotEqual(oldPath, newPath);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(newPath)).StatusCode);
    }
}
