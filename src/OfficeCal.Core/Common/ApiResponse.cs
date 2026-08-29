namespace OfficeCal.Core.Common;

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string Message { get; set; } = "";
    public List<string> Errors { get; set; } = new();
}

public static class ApiResponse
{
    public static ApiResponse<T> Ok<T>(T data, string message = "")
        => new() { Success = true, Data = data, Message = message };

    /// <summary>
    /// 只回訊息、沒有資料的成功回應。C# 的多載解析在兩者皆適用時偏好非泛型版本，
    /// 因此 <c>ApiResponse.Ok("已登出")</c> 會走到這裡而不是 Ok&lt;string&gt;。
    /// </summary>
    public static ApiResponse<object?> Ok(string message = "")
        => new() { Success = true, Data = null, Message = message };

    public static ApiResponse<object?> Fail(string message, IEnumerable<string>? errors = null,
                                            object? data = null)
        => new() { Success = false, Data = data, Message = message, Errors = errors?.ToList() ?? new() };
}
