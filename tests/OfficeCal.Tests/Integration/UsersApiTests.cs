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
}
