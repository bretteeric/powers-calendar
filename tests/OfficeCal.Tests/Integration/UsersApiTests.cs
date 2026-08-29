using System.Net;
using System.Net.Http.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Infrastructure;
using OfficeCal.Tests.Fixtures;
using Xunit;

namespace OfficeCal.Tests.Integration;

[Collection("Api")]
public class UsersApiTests
{
    private readonly ApiFactory _api;
    public UsersApiTests(ApiFactory api) => _api = api;

    [Fact]
    public async Task 任何已登入者都能取得與會者選單()
    {
        await _api.EnsureEmployeeAsync("E300", "王小明");
        var employee = await _api.LoginAsync("E300", ApiFactory.EmployeePassword);

        var res = await employee.GetFromJsonAsync<ApiResponse<List<UserPickerDto>>>(
            "/api/v1/users/picker", ApiFactory.Json);

        Assert.Contains(res!.Data!, u => u.EmployeeNo == "E300");
    }

    [Fact]
    public async Task 非管理員不能維護員工帳號()
    {
        await _api.EnsureEmployeeAsync("E301", "李小華");
        var employee = await _api.LoginAsync("E301", ApiFactory.EmployeePassword);

        var res = await employee.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            EmployeeNo = "E999", DisplayName = "偷建的帳號",
            Email = "e999@corp.local", Role = "Admin", Password = "Whatever@123",
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 管理員可建立帳號並用新帳號登入()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var create = await admin.PostAsJsonAsync("/api/v1/users", new CreateUserRequest
        {
            EmployeeNo = "E400", DisplayName = "新進員工",
            Email = "e400@corp.local", Role = "Employee", Password = "NewHire@123",
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var newbie = await _api.LoginAsync("E400", "NewHire@123");
        var me = await newbie.GetFromJsonAsync<ApiResponse<MeDto>>("/api/v1/me", ApiFactory.Json);
        Assert.Equal("新進員工", me!.Data!.DisplayName);
        Assert.False(me.Data.IsAdmin);
    }

    [Fact]
    public async Task 員工編號重複回四百()
    {
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        var req = new CreateUserRequest
        {
            EmployeeNo = DbSeeder.AdminEmployeeNo, DisplayName = "撞號",
            Email = "dup@corp.local", Role = "Employee", Password = "Whatever@123",
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/users", req)).StatusCode);
    }

    [Fact]
    public async Task 管理員可重設密碼且新密碼可登入()
    {
        var id = await _api.EnsureEmployeeAsync("E401", "忘記密碼的人");
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        var res = await admin.PostAsJsonAsync($"/api/v1/users/{id}/reset-password",
            new ResetPasswordRequest { NewPassword = "Reset@12345" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var client = await _api.LoginAsync("E401", "Reset@12345");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task 本人可修改密碼但舊密碼錯誤時回四百()
    {
        await _api.EnsureEmployeeAsync("E402", "改密碼的人");
        var client = await _api.LoginAsync("E402", ApiFactory.EmployeePassword);

        var wrong = await client.PostAsJsonAsync("/api/v1/me/change-password",
            new ChangePasswordRequest { CurrentPassword = "wrong", NewPassword = "Brand@New123" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await client.PostAsJsonAsync("/api/v1/me/change-password",
            new ChangePasswordRequest
            {
                CurrentPassword = ApiFactory.EmployeePassword, NewPassword = "Brand@New123",
            });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var relogin = await _api.LoginAsync("E402", "Brand@New123");
        Assert.Equal(HttpStatusCode.OK, (await relogin.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task 停用帳號後不能再登入()
    {
        var id = await _api.EnsureEmployeeAsync("E403", "即將離職");
        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);

        await admin.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "即將離職", Email = "e403@corp.local",
            Role = "Employee", IsActive = false,
        });

        var client = _api.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest { EmployeeNo = "E403", Password = ApiFactory.EmployeePassword });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // --- 任務 14 審查第 1 輪重要 1：5 個端點只有 1 個有非 Admin 的 403 測試保護，補齊其餘 3 個 ---

    [Fact]
    public async Task 非管理員不能取得員工清單()
    {
        await _api.EnsureEmployeeAsync("E301", "李小華");
        var employee = await _api.LoginAsync("E301", ApiFactory.EmployeePassword);

        var res = await employee.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 非管理員不能編輯員工帳號()
    {
        var targetId = await _api.EnsureEmployeeAsync("E304", "被瞄準的帳號一");
        await _api.EnsureEmployeeAsync("E301", "李小華");
        var employee = await _api.LoginAsync("E301", ApiFactory.EmployeePassword);

        var res = await employee.PutAsJsonAsync($"/api/v1/users/{targetId}", new UpdateUserRequest
        {
            DisplayName = "被偷改的帳號", Email = "e304@corp.local",
            Role = "Admin", IsActive = true,
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 非管理員不能重設他人密碼()
    {
        var targetId = await _api.EnsureEmployeeAsync("E305", "被瞄準的帳號二");
        await _api.EnsureEmployeeAsync("E301", "李小華");
        var employee = await _api.LoginAsync("E301", ApiFactory.EmployeePassword);

        var res = await employee.PostAsJsonAsync($"/api/v1/users/{targetId}/reset-password",
            new ResetPasswordRequest { NewPassword = "Hijacked@123" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // --- 任務 14 審查第 1 輪重要 2：停用／降級對既有 session 完全無效，須靠 OnValidatePrincipal 擋下 ---

    [Fact]
    public async Task 使用者被停用後既有session的下一次呼叫回四百零一()
    {
        var id = await _api.EnsureEmployeeAsync("E306", "在線後被停用的人");
        var client = await _api.LoginAsync("E306", ApiFactory.EmployeePassword);

        // 先確認這個 session 本來是正常可用的
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        await admin.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "在線後被停用的人", Email = "e306@corp.local",
            Role = "Employee", IsActive = false,
        });

        var res = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        // 規格 3：401 也要走統一信封（任務 8 修過一次，這裡順帶守住不要回歸）
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>(ApiFactory.Json);
        Assert.False(body!.Success);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
    }

    [Fact]
    public async Task 管理員被降級後既有session不能再呼叫管理端點()
    {
        var id = await _api.EnsureEmployeeAsync("E307", "在線後被降級的管理員",
            OfficeCal.Core.Enums.UserRole.Admin);
        var client = await _api.LoginAsync("E307", ApiFactory.EmployeePassword);

        // 先確認這個 session 本來是 Admin，能打管理端點
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/users")).StatusCode);

        var admin = await _api.LoginAsync(DbSeeder.AdminEmployeeNo, DbSeeder.AdminInitialPassword);
        await admin.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "在線後被降級的管理員", Email = "e307@corp.local",
            Role = "Employee", IsActive = true,
        });

        // 角色宣告與資料庫現值不符，principal 被拒絕，走 401（不是 403 沒有權限）
        var res = await client.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "想改回自己是Admin", Email = "e307@corp.local",
            Role = "Admin", IsActive = true,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // --- 任務 14 審查第 1 輪重要 3：管理員不能停用或降級自己，避免系統失去所有管理入口 ---

    [Fact]
    public async Task 管理員不能停用或降級自己的帳號()
    {
        var id = await _api.EnsureEmployeeAsync("E308", "自我保護測試管理員",
            OfficeCal.Core.Enums.UserRole.Admin);
        var self = await _api.LoginAsync("E308", ApiFactory.EmployeePassword);

        var deactivateSelf = await self.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "自我保護測試管理員", Email = "e308@corp.local",
            Role = "Admin", IsActive = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, deactivateSelf.StatusCode);

        var demoteSelf = await self.PutAsJsonAsync($"/api/v1/users/{id}", new UpdateUserRequest
        {
            DisplayName = "自我保護測試管理員", Email = "e308@corp.local",
            Role = "Employee", IsActive = true,
        });
        Assert.Equal(HttpStatusCode.BadRequest, demoteSelf.StatusCode);

        // 兩次都被擋下，帳號應該仍是啟用中的 Admin，能正常呼叫管理端點
        var stillWorks = await self.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }
}
