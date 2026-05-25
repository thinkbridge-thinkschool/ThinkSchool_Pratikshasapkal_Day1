using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Models;
using QuotesApi.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace QuotesApi.Services;

/// <summary>
/// Singleton service: JWT access-token creation, refresh-token generation, and token hashing.
/// Injecting IOptions&lt;JwtOptions&gt; (not IOptionsSnapshot) is deliberate — the signing key
/// is baked into JwtBearerOptions at startup and cannot change without a restart anyway,
/// so the singleton lifetime matches.
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly JwtOptions _opts;

    public TokenService(IOptions<JwtOptions> options)
    {
        _opts = options.Value;
    }

    public TimeSpan AccessTokenLifetime  => _opts.AccessTokenLifetime;
    public TimeSpan RefreshTokenLifetime => _opts.RefreshTokenLifetime;

    public string CreateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim("scope", "quotes.write")
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Key));

        var token = new JwtSecurityToken(
            issuer:             _opts.Issuer,
            audience:           _opts.Audience,
            claims:             claims,
            expires:            DateTime.UtcNow.Add(_opts.AccessTokenLifetime),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLower();
    }
}
