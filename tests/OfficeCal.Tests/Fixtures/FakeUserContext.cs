using OfficeCal.Services;

namespace OfficeCal.Tests.Fixtures;

public class FakeUserContext : IUserContext
{
    public bool IsAuthenticated { get; set; } = true;
    public int UserId { get; set; }
    public string DisplayName { get; set; } = "";
    public bool IsAdmin { get; set; }
}
