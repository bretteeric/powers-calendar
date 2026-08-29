using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Core.Common;
using OfficeCal.Core.Dtos;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;
    private readonly IUserContext _me;

    public NotificationsController(INotificationService notifications, IUserContext me)
        => (_notifications, _me) = (notifications, me);

    [HttpGet("")]
    [ProducesResponseType(typeof(ApiResponse<NotificationListDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAsync([FromQuery] bool unreadOnly = false,
                                               [FromQuery] int take = 30,
                                               CancellationToken ct = default)
    {
        var items = await _notifications.ListAsync(_me.UserId, unreadOnly, Math.Clamp(take, 1, 100), ct);
        var unread = await _notifications.UnreadCountAsync(_me.UserId, ct);
        return Ok(ApiResponse.Ok(new NotificationListDto { Items = items, UnreadCount = unread }));
    }

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkReadAsync(int id, CancellationToken ct)
    {
        await _notifications.MarkReadAsync(id, _me.UserId, ct);
        return Ok(ApiResponse.Ok("已標記為已讀"));
    }
}
