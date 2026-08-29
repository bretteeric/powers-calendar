using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

/// <summary>與會者選單用的最小資訊，任何已登入者可讀。</summary>
public class UserPickerDto
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string EmployeeNo { get; set; } = "";
    public string? DepartmentName { get; set; }
}

/// <summary>員工管理後台用，含角色與啟用狀態。</summary>
public class UserAdminDto : UserPickerDto
{
    public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    public string Role { get; set; } = "";
    public bool IsActive { get; set; }
}

public class CreateUserRequest
{
    [Required][StringLength(20)] public string EmployeeNo { get; set; } = "";
    [Required][StringLength(50)] public string DisplayName { get; set; } = "";
    [Required][EmailAddress][StringLength(100)] public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    [Required] public string Role { get; set; } = "Employee";
    [Required][StringLength(100, MinimumLength = 8, ErrorMessage = "密碼至少 8 個字元")]
    public string Password { get; set; } = "";
}

public class UpdateUserRequest
{
    [Required][StringLength(50)] public string DisplayName { get; set; } = "";
    [Required][EmailAddress][StringLength(100)] public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    [Required] public string Role { get; set; } = "Employee";
    public bool IsActive { get; set; } = true;
}

public class ResetPasswordRequest
{
    [Required][StringLength(100, MinimumLength = 8, ErrorMessage = "密碼至少 8 個字元")]
    public string NewPassword { get; set; } = "";
}

public class DepartmentDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}
