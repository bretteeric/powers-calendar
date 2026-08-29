using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/departments")]
[Authorize]
public class DepartmentsController : ControllerBase
{
    private readonly IUserService _users;
    public DepartmentsController(IUserService users) => _users = users;

    [HttpGet("")]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.ListDepartmentsAsync(ct)));
}
