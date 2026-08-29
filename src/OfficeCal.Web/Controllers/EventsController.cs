using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Core.Enums;
using OfficeCal.Core.Exceptions;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/events")]
[Authorize]
public class EventsController : ControllerBase
{
    private readonly IEventService _events;
    private readonly IIcsService _ics;

    public EventsController(IEventService events, IIcsService ics)
        => (_events, _ics) = (events, ics);

    /// <summary>把查詢字串的 mode 轉成列舉。未指定時視為 series。</summary>
    private static EditMode ParseMode(string? mode) => (mode ?? "series").ToLowerInvariant() switch
    {
        "single" => EditMode.Single,
        "series" => EditMode.Series,
        _ => throw new ValidationException("mode 必須是 single 或 series"),
    };

    private static CalendarScope ParseScope(string? scope) => (scope ?? "me").ToLowerInvariant() switch
    {
        "me" => CalendarScope.Me,
        "room" => CalendarScope.Room,
        "all" => CalendarScope.All,
        _ => throw new ValidationException("scope 必須是 me、room 或 all"),
    };

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<List<OccurrenceDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRangeAsync([FromQuery] DateTime from, [FromQuery] DateTime to,
                                                    [FromQuery] string? scope, [FromQuery] int? roomId,
                                                    CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.GetRangeAsync(from, to, ParseScope(scope), roomId, ct)));

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(ApiResponse<EventDetailDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDetailAsync(int id, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.GetDetailAsync(id, ct)));

    [HttpPost("")]
    [ProducesResponseType(typeof(ApiResponse<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAsync([FromBody] CreateEventRequest req, CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.CreateAsync(req, ct), "已建立事件"));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateAsync(int id, [FromQuery] string? mode,
                                                  [FromBody] UpdateEventRequest req,
                                                  CancellationToken ct)
    {
        await _events.UpdateAsync(id, ParseMode(mode), req, ct);
        return Ok(ApiResponse.Ok("已更新事件"));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> CancelAsync(int id, [FromQuery] string? mode,
                                                  [FromQuery] int? occurrenceId, CancellationToken ct)
    {
        await _events.CancelAsync(id, ParseMode(mode), occurrenceId, ct);
        return Ok(ApiResponse.Ok("已取消事件"));
    }

    [HttpPost("check-attendees")]
    [ProducesResponseType(typeof(ApiResponse<List<AttendeeConflictDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckAttendeesAsync([FromBody] AttendeeConflictRequest req,
                                                          CancellationToken ct)
        => Ok(ApiResponse.Ok(await _events.CheckAttendeesAsync(req, ct)));

    /// <summary>回傳原始 .ics 文字，不套用統一信封——行事曆軟體要的是檔案本身。</summary>
    [HttpGet("{id:int}/ics")]
    public async Task<IActionResult> ExportIcsAsync(int id, CancellationToken ct)
    {
        var ics = await _ics.ExportEventAsync(id, ct);
        return File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8",
                    $"event-{id}.ics");
    }
}
