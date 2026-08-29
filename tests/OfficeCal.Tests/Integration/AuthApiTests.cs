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
public class AuthApiTests
{
    private readonly ApiFactory _api;
    public AuthApiTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task 正確帳密可登入並取得個人資料()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<MeDto>>(ApiFactory.Json);
        Assert.True(body!.Success);
        Assert.Equal(DbSeeder.AdminEmployeeNo, body.Data!.EmployeeNo);
        Assert.True(body.Data.IsAdmin);
        Assert.Contains("/feeds/", body.Data.FeedUrl);
        Assert.EndsWith(".ics", body.Data.FeedUrl);
    }

    [Fact]
    public async Task 密碼錯誤回四百且信封標示失敗()
    {
        var client = _api.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { EmployeeNo = DbSeeder.AdminEmployeeNo, Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>(ApiFactory.Json);
        Assert.False(body!.Success);
        Assert.Equal("員工編號或密碼錯誤", body.Message);
    }

    [Fact]
    public async Task 未登入呼叫受保護端點回四百零一()
    {
        var client = _api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing
            .WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        // 規格 3：所有 /api/v1/* 回應都要走統一信封，401/403 也不例外。
        // 這條斷言就是護欄——只查狀態碼會漏掉 body 是空的這種缺口。
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>(ApiFactory.Json);
        Assert.False(body!.Success);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }

    [Fact]
    public async Task 停用帳號不能登入()
    {
        await using (var db = _api.CreateContext())
        {
            var u = await db.Users.FirstAsync(x => x.EmployeeNo == DbSeeder.AdminEmployeeNo);
            u.IsActive = false;
            await db.SaveChangesAsync();
        }

        try
        {
            var client = _api.CreateClient();
            var res = await client.PostAsJsonAsync("/api/v1/auth/login",
                new LoginRequest
                {
                    EmployeeNo = DbSeeder.AdminEmployeeNo,
                    Password = DbSeeder.AdminInitialPassword,
                });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        finally
        {
            // 還原，避免影響同集合的其他測試——放在 finally 裡，即使上面的斷言失敗
            // 也不會讓管理員帳號永久停用在共享的 [Collection("Api")] 資料庫中。
            await using var db = _api.CreateContext();
            var u = await db.Users.FirstAsync(x => x.EmployeeNo == DbSeeder.AdminEmployeeNo);
            u.IsActive = true;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 登出後Cookie失效()
    {
        var client = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var logout = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var after = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
