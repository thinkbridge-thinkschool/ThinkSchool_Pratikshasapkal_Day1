using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Quotes.Tests.Integration;

/// <summary>
/// Mints JWT tokens for auth/authz integration tests without going through
/// the login endpoint — gives tests precise control over claims and expiry.
/// The signing key matches appsettings.Testing.json so the API under test
/// accepts the tokens as valid.
/// </summary>
public static class JwtTestHelper
{
    // Must match appsettings.Testing.json Jwt:Key exactly.
    private const string TestSigningKey =
        "integration-tests-only-signing-key-not-for-production-use";

    private const string Issuer   = "QuotesApi";
    private const string Audience = "QuotesApi";

    /// <summary>
    /// Creates a signed, non-expired JWT.
    /// </summary>
    /// <param name="includeWriteScope">
    /// When <c>true</c> the token includes <c>scope=quotes.write</c> so the
    /// <c>can-edit-quotes</c> policy is satisfied.  When <c>false</c> the scope
    /// claim is omitted, which triggers a 403 from that policy.
    /// </param>
    public static string CreateToken(bool includeWriteScope = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "9999"),
            new(ClaimTypes.Email,          "testuser@example.com"),
        };

        if (includeWriteScope)
            claims.Add(new Claim("scope", "quotes.write"));

        return BuildToken(claims, expires: DateTime.UtcNow.AddMinutes(15));
    }

    /// <summary>
    /// Creates a validly-signed JWT whose <c>exp</c> is one hour in the past,
    /// triggering <see cref="SecurityTokenExpiredException"/> in JwtBearer.
    /// </summary>
    public static string CreateExpiredToken()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "9999"),
            new(ClaimTypes.Email,          "testuser@example.com"),
            new("scope",                   "quotes.write"),
        };

        return BuildToken(claims, expires: DateTime.UtcNow.AddHours(-1));
    }

    private static string BuildToken(IEnumerable<Claim> claims, DateTime expires)
    {
        var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer:             Issuer,
            audience:           Audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow.AddMinutes(-1),
            expires:            expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
