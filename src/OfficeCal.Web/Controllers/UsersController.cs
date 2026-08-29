using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserService _users;
    public UsersController(IUserService users) => _users = users;

    /// <summary>與會者多選用。任何已登入者可讀，只回傳姓名與部門。</summary>
    [HttpGet("picker")]
    [ProducesResponseType(typeof(ApiResponse<List<UserPickerDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> PickerAsync(CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.ListForPickerAsync(ct)));

    [HttpGet("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.ListForAdminAsync(ct)));

    [HttpPost("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> CreateAsync([FromBody] CreateUserRequest req, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _users.CreateAsync(req, ct), "已建立帳號"));

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateUserRequest req,
                                                  CancellationToken ct)
    {
        await _users.UpdateAsync(id, req, ct);
        return Ok(ApiResponse.Ok("已更新帳號"));
    }

    [HttpPost("{id:int}/reset-password")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> ResetPasswordAsync(int id, [FromBody] ResetPasswordRequest req,
                                                         CancellationToken ct)
    {
        await _users.ResetPasswordAsync(id, req.NewPassword, ct);
        return Ok(ApiResponse.Ok("已重設密碼"));
    }
}
