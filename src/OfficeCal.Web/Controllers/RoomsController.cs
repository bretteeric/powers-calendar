using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/rooms")]
[Authorize]
public class RoomsController : ControllerBase
{
    private readonly IRoomService _rooms;
    public RoomsController(IRoomService rooms) => _rooms = rooms;

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<List<RoomDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync([FromQuery] bool includeInactive = false,
                                                CancellationToken ct = default)
        => Ok(ApiResponse.Ok(await _rooms.ListAsync(activeOnly: !includeInactive, ct)));

    [HttpGet("availability")]
    [ProducesResponseType(typeof(ApiResponse<List<RoomAvailabilityDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AvailabilityAsync([FromQuery] DateOnly date,
                                                        [FromQuery] int? capacity,
                                                        CancellationToken ct)
        => Ok(ApiResponse.Ok(await _rooms.GetAvailabilityAsync(date, capacity, ct)));

    [HttpPost("")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> CreateAsync([FromBody] RoomRequest req, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _rooms.CreateAsync(req, ct), "已新增會議廳"));

    [HttpPut("{id:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] RoomRequest req,
                                                  CancellationToken ct)
    {
        await _rooms.UpdateAsync(id, req, ct);
        return Ok(ApiResponse.Ok("已更新會議廳"));
    }
}
