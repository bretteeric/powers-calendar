using System.ComponentModel.DataAnnotations;

namespace OfficeCal.Core.Dtos;

public class LoginRequest
{
    [Required(ErrorMessage = "請輸入員工編號")]
    [StringLength(20)]
    public string EmployeeNo { get; set; } = "";

    [Required(ErrorMessage = "請輸入密碼")]
    [StringLength(100)]
    public string Password { get; set; } = "";
}

public class MeDto
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? DepartmentName { get; set; }
    public string Role { get; set; } = "";
    public bool IsAdmin { get; set; }
    /// <summary>個人訂閱 feed 的完整網址，供個人設定頁顯示與複製。</summary>
    public string FeedUrl { get; set; } = "";
}

public class ChangePasswordRequest
{
    [Required] public string CurrentPassword { get; set; } = "";
    [Required][StringLength(100, MinimumLength = 8, ErrorMessage = "新密碼至少 8 個字元")]
    public string NewPassword { get; set; } = "";
}
