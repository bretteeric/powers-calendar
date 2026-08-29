using OfficeCal.Core.Dtos;

namespace OfficeCal.Services;

public interface IUserService
{
    Task<List<UserPickerDto>> ListForPickerAsync(CancellationToken ct = default);
    Task<List<UserAdminDto>> ListForAdminAsync(CancellationToken ct = default);
    Task<int> CreateAsync(CreateUserRequest req, CancellationToken ct = default);
    Task UpdateAsync(int id, UpdateUserRequest req, CancellationToken ct = default);
    Task ResetPasswordAsync(int id, string newPassword, CancellationToken ct = default);
    Task ChangeOwnPasswordAsync(int userId, ChangePasswordRequest req, CancellationToken ct = default);
    /// <summary>重新產生訂閱 token，舊網址即刻失效。回傳新 token。</summary>
    Task<string> ResetFeedTokenAsync(int userId, CancellationToken ct = default);
    Task<List<DepartmentDto>> ListDepartmentsAsync(CancellationToken ct = default);
}
