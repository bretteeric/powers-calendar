using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OfficeCal.Web.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    public void OnGet() { }
}
