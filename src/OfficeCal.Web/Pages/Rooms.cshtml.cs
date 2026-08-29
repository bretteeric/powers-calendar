using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages;

[Authorize]
public class RoomsModel : PageModel
{
    public void OnGet() { }
}
