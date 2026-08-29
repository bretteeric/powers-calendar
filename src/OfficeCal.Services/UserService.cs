using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure;
using OfficeCal.Infrastructure.Repositories;

namespace OfficeCal.Services;

public class UserService : IUserService
{
    private readonly OfficeCalDbContext _db;
    private readonly IUserRepository _users;
    private readonly IPasswordService _passwords;
    private readonly IUserContext _me;

    public UserService(OfficeCalDbContext db, IUserRepository users, IPasswordService passwords,
                       IUserContext me)
        => (_db, _users, _passwords, _me) = (db, users, passwords, me);

    public async Task<List<UserPickerDto>> ListForPickerAsync(CancellationToken ct = default)
        => (await _users.ListAsync(ct))
            .Where(u => u.IsActive)
            .Select(u => new UserPickerDto
            {
                Id = u.Id, DisplayName = u.DisplayName, EmployeeNo = u.EmployeeNo,
                DepartmentName = u.Department?.Name,
            })
            .ToList();

    public async Task<List<UserAdminDto>> ListForAdminAsync(CancellationToken ct = default)
        => (await _users.ListAsync(ct))
            .Select(u => new UserAdminDto
            {
                Id = u.Id, DisplayName = u.DisplayName, EmployeeNo = u.EmployeeNo,
                DepartmentName = u.Department?.Name, DepartmentId = u.DepartmentId,
                Email = u.Email, Role = u.Role.ToString(), IsActive = u.IsActive,
            })
            .ToList();

    public async Task<int> CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        var employeeNo = req.EmployeeNo.Trim();
        var email = req.Email.Trim();

        if (await _db.Users.AnyAsync(u => u.EmployeeNo == employeeNo, ct))
            throw new ValidationException($"員工編號「{employeeNo}」已存在");
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
            throw new ValidationException($"Email「{email}」已被使用");

        var user = new User
        {
            EmployeeNo = employeeNo,
            DisplayName = req.DisplayName.Trim(),
            Email = email,
            DepartmentId = req.DepartmentId,
            Role = ParseRole(req.Role),
            IcsFeedToken = _passwords.NewFeedToken(),
            IsActive = true,
        };
        user.PasswordHash = _passwords.Hash(user, req.Password);

        _users.Add(user);
        await _db.SaveChangesAsync(ct);
        return user.Id;
    }

    public async Task UpdateAsync(int id, UpdateUserRequest req, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(id, ct) ?? throw new NotFoundException("找不到使用者");
        var email = req.Email.Trim();
        var newRole = ParseRole(req.Role);

        // 任務 14 審查重要 3：這是唯一能移除 Admin 身分的端點，若允許自我停用／自我降級，
        // 一旦系統只剩一名 Admin 就會失去所有管理入口，且無應用層復原路徑（DbSeeder 只補
        // 「帳號不存在」，不會把既有帳號改回 Admin）。擋掉對自己的操作即可保證此情境不會發生。
        if (id == _me.UserId && (!req.IsActive || newRole != UserRole.Admin))
            throw new ValidationException("不能停用或降級自己的帳號");

        if (await _db.Users.AnyAsync(u => u.Email == email && u.Id != id, ct))
            throw new ValidationException($"Email「{email}」已被使用");

        user.DisplayName = req.DisplayName.Trim();
        user.Email = email;
        user.DepartmentId = req.DepartmentId;
        user.Role = newRole;
        user.IsActive = req.IsActive;   // 停用帳號不能登入，既有事件保留
        await _db.SaveChangesAsync(ct);
    }

    public async Task ResetPasswordAsync(int id, string newPassword, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(id, ct) ?? throw new NotFoundException("找不到使用者");
        user.PasswordHash = _passwords.Hash(user, newPassword);
        await _db.SaveChangesAsync(ct);
    }

    public async Task ChangeOwnPasswordAsync(int userId, ChangePasswordRequest req,
                                             CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct) ?? throw new NotFoundException("找不到使用者");

        if (!_passwords.Verify(user, req.CurrentPassword))
            throw new ValidationException("目前密碼不正確");

        user.PasswordHash = _passwords.Hash(user, req.NewPassword);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string> ResetFeedTokenAsync(int userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct) ?? throw new NotFoundException("找不到使用者");
        user.IcsFeedToken = _passwords.NewFeedToken();
        await _db.SaveChangesAsync(ct);
        return user.IcsFeedToken;
    }

    public async Task<List<DepartmentDto>> ListDepartmentsAsync(CancellationToken ct = default)
        => await _db.Departments.AsNoTracking().OrderBy(d => d.Name)
            .Select(d => new DepartmentDto { Id = d.Id, Name = d.Name, IsActive = d.IsActive })
            .ToListAsync(ct);

    private static UserRole ParseRole(string role)
        => Enum.TryParse<UserRole>(role, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ValidationException("角色必須是 Employee 或 Admin");
}
