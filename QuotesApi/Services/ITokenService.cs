using QuotesApi.Models;

namespace QuotesApi.Services;

public interface ITokenService
{
    string CreateAccessToken(User user);
    string GenerateRefreshToken();
    string HashToken(string token);
    TimeSpan AccessTokenLifetime { get; }
    TimeSpan RefreshTokenLifetime { get; }
}
