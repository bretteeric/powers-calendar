using Microsoft.AspNetCore.Identity;
using OfficeCal.Core.Entities;

namespace OfficeCal.Services;

public interface IPasswordService
{
    string Hash(User user, string plainPassword);
    bool Verify(User user, string plainPassword);
    /// <summary>產生訂閱 feed 的隨機 token（URL 安全，43 字元）。</summary>
    string NewFeedToken();
}

public class PasswordService : IPasswordService
{
    // 規格 6.2：不引入完整 ASP.NET Core Identity，只借用它的密碼雜湊器。
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(User user, string plainPassword) => _hasher.HashPassword(user, plainPassword);

    public bool Verify(User user, string plainPassword)
        => _hasher.VerifyHashedPassword(user, user.PasswordHash, plainPassword)
           != PasswordVerificationResult.Failed;

    public string NewFeedToken()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
                  .Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
