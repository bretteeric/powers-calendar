using Microsoft.EntityFrameworkCore;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;

namespace OfficeCal.Infrastructure;

public static class DbSeeder
{
    public const string AdminEmployeeNo = "A0001";
    public const string AdminInitialPassword = "Admin@12345";

    /// <summary>可重複執行。hashPassword 由呼叫端提供，避免 Infrastructure 依賴 Services。</summary>
    public static async Task SeedAsync(OfficeCalDbContext db, Func<User, string, string> hashPassword,
                                       Func<string> newFeedToken)
    {
        await db.Database.MigrateAsync();

        if (!await db.Departments.AnyAsync())
        {
            db.Departments.AddRange(
                new Department { Name = "資訊部" },
                new Department { Name = "業務部" },
                new Department { Name = "管理部" });
            await db.SaveChangesAsync();
        }

        if (!await db.Users.AnyAsync(u => u.EmployeeNo == AdminEmployeeNo))
        {
            var it = await db.Departments.FirstAsync(d => d.Name == "資訊部");
            var admin = new User
            {
                EmployeeNo = AdminEmployeeNo,
                DisplayName = "系統管理員",
                Email = "admin@corp.local",
                DepartmentId = it.Id,
                Role = UserRole.Admin,
                IcsFeedToken = newFeedToken(),
                IsActive = true,
            };
            admin.PasswordHash = hashPassword(admin, AdminInitialPassword);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
        }

        if (!await db.Rooms.AnyAsync())
        {
            db.Rooms.AddRange(
                new Room { Name = "A 棟 3F 大會議廳", Location = "A 棟 3 樓", Capacity = 40,
                           Equipment = "投影機、視訊設備、白板" },
                new Room { Name = "A 棟 3F 小會議室", Location = "A 棟 3 樓", Capacity = 8,
                           Equipment = "電視螢幕" },
                new Room { Name = "B 棟 2F 討論室", Location = "B 棟 2 樓", Capacity = 6,
                           Equipment = "白板" });
            await db.SaveChangesAsync();
        }
    }
}
