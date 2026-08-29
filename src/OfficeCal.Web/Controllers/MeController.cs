using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Entities;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Infrastructure.Repositories;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/me")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly IUserRepository _users;
    private readonly IUserContext _me;

    public MeController(IUserRepository users, IUserContext me) => (_users, _me) = (users, me);

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<MeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(CancellationToken ct)
    {
        var user = await _users.GetByIdAsync(_me.UserId, ct)
                   ?? throw new NotFoundException("找不到使用者");
        return Ok(ApiResponse.Ok(ToDto(user, Request)));
    }

    public static MeDto ToDto(User user, HttpRequest request) => new()
    {
        Id = user.Id,
        EmployeeNo = user.EmployeeNo,
        DisplayName = user.DisplayName,
        Email = user.Email,
        DepartmentName = user.Department?.Name,
        Role = user.Role.ToString(),
        IsAdmin = user.Role == UserRole.Admin,
        FeedUrl = $"{request.Scheme}://{request.Host}/feeds/{user.IcsFeedToken}.ics",
    };
}
