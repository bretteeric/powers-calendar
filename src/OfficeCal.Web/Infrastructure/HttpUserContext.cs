using System.Security.Claims;
using OfficeCal.Core.Enums;
using OfficeCal.Services;

namespace OfficeCal.Web.Infrastructure;

public class HttpUserContext : IUserContext
{
    private readonly IHttpContextAccessor _accessor;
    public HttpUserContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public int UserId => int.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id
        : throw new InvalidOperationException("目前沒有登入的使用者");

    public string DisplayName => Principal?.FindFirstValue(ClaimTypes.Name) ?? "";

    public bool IsAdmin => Principal?.IsInRole(nameof(UserRole.Admin)) == true;
}
