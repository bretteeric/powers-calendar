using System.Text.Json;
using OfficeCal.Core.Common;
using OfficeCal.Core.Exceptions;

namespace OfficeCal.Web.Middleware;

/// <summary>
/// 領域例外 → HTTP 狀態碼 + 統一回傳信封。Controller 內一律不寫 try/catch（規格 9）。
/// </summary>
public class GlobalExceptionMiddleware
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
        => (_next, _logger) = (next, logger);

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            if (ctx.Response.HasStarted) throw;

            var (status, payload) = Map(ex);

            if (status == StatusCodes.Status500InternalServerError)
                _logger.LogError(ex, "未預期例外：{Path}", ctx.Request.Path);

            ctx.Response.Clear();
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
        }
    }

    private static (int, ApiResponse<object?>) Map(Exception ex) => ex switch
    {
        ValidationException v => (StatusCodes.Status400BadRequest,
            ApiResponse.Fail(v.Message, v.Errors)),

        NotFoundException n => (StatusCodes.Status404NotFound,
            ApiResponse.Fail(n.Message)),

        ForbiddenException f => (StatusCodes.Status403Forbidden,
            ApiResponse.Fail(f.Message)),

        ConflictException c => (StatusCodes.Status409Conflict,
            ApiResponse.Fail(c.Message, null, new { conflicts = c.Conflicts })),

        _ => (StatusCodes.Status500InternalServerError,
            ApiResponse.Fail("系統發生未預期的錯誤，請稍後再試")),
    };
}
