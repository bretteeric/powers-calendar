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

/// <summary>
/// 這個請求承載權限：Role 與 IsActive 是唯一能移除 Admin 身分、唯一能停用帳號的入口。
/// 因此兩者的預設值都刻意指向「不可用」而不是「安全的一般值」——[Required] 對已有非空
/// 預設值的 string 形同虛設，若 Role 預設 "Employee"、IsActive 預設 true，只帶
/// displayName + email 的 PUT 就會靜默把 Admin 降級、把已停用帳號重新啟用。
/// Role = "" 讓 [Required] 擋下；IsActive 用 bool? 讓「沒送」與「送 false」可以區分，
/// 由 UserService 要求非 null。
/// </summary>
public class UpdateUserRequest
{
    [Required][StringLength(50)] public string DisplayName { get; set; } = "";
    [Required][EmailAddress][StringLength(100)] public string Email { get; set; } = "";
    public int? DepartmentId { get; set; }
    [Required] public string Role { get; set; } = "";
    public bool? IsActive { get; set; }
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
