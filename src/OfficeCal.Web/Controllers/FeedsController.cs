using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeCal.Services;

namespace OfficeCal.Web.Controllers;

/// <summary>
/// 個人訂閱 feed。匿名端點，以 token 授權——行事曆軟體訂閱時無法攜帶登入狀態。
/// 不套用統一信封。
/// </summary>
[ApiController]
[Route("feeds")]
[AllowAnonymous]
public class FeedsController : ControllerBase
{
    private readonly IIcsService _ics;
    public FeedsController(IIcsService ics) => _ics = ics;

    [HttpGet("{token}.ics")]
    public async Task<IActionResult> GetAsync(string token, CancellationToken ct)
    {
        var ics = await _ics.BuildFeedAsync(token, ct);
        return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", "calendar.ics");
    }
}
