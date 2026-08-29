namespace OfficeCal.Services;

/// <summary>Service 層取得目前登入者的唯一管道，避免直接依賴 HttpContext。</summary>
public interface IUserContext
{
    bool IsAuthenticated { get; }
    /// <summary>未登入時存取會丟 InvalidOperationException。</summary>
    int UserId { get; }
    string DisplayName { get; }
    bool IsAdmin { get; }
}
