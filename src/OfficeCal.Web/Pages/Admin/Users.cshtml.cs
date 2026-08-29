using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages.Admin;

[Authorize(Policy = "Admin")]
public class UsersModel : PageModel
{
    public void OnGet() { }
}
