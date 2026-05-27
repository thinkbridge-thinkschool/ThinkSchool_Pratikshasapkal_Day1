using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Quotes.Tests.Integration;

/// <summary>
/// Generates test JWTs signed with the same symmetric key as appsettings.Testing.json.
/// Allows integration tests to craft tokens with exact, controlled properties (missing
/// scope claims, already-expired) without going through the real login endpoint.
///
/// Security note: the key here is the test-only key in appsettings.Testing.json.
/// It is never used in staging or production.
/// </summary>
internal static class JwtTestHelper
{
    // ── Constants must exactly match appsettings.Testing.json → Jwt section ─────
    // Changing the key here without changing appsettings.Testing.json will cause
    // the generated tokens to fail JwtBearer validation (invalid signature → 401).
    private const string TestKey      = "integration-tests-only-signing-key-not-for-production-use";
    private const string TestIssuer   = "QuotesApi";
    private const string TestAudience = "QuotesApi";

    /// <summary>
    /// Creates a validly-signed JWT for a fictitious test identity.
    /// The user does NOT need to exist in the database — authentication middleware
    /// validates the token structure and signature only; no DB lookup occurs.
    /// </summary>
    /// <param name="userId">
    ///   Value of the <see cref="ClaimTypes.NameIdentifier"/> (sub) claim.
    ///   Use a value that does not collide with seeded users (default: "9999").
    /// </param>
    /// <param name="email">Value of the <see cref="ClaimTypes.Email"/> claim.</param>
    /// <param name="includeWriteScope">
    ///   When <c>true</c> (default) adds <c>scope=quotes.write</c> — the claim
    ///   required by the <c>can-edit-quotes</c> policy in Program.cs.
    ///   Set to <c>false</c> to simulate an authenticated-but-under-privileged caller:
    ///   the token will pass JwtBearer (authenticated), but the policy check will fail (403).
    /// </param>
    /// <param name="lifetime">
    ///   How long the token is valid from now.
    ///   Pass a <b>negative</b> value to create an already-expired token,
    ///   e.g. <c>TimeSpan.FromHours(-1)</c>.
    ///   Defaults to 15 minutes (normal access token lifetime) when <c>null</c>.
    /// </param>
    public static string CreateToken(
        string    userId            = "9999",
        string    email             = "testonly@example.com",
        bool      includeWriteScope = true,
        TimeSpan? lifetime          = null)
    {
        var delta = lifetime ?? TimeSpan.FromMinutes(15);
        var now   = DateTime.UtcNow;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Email, email)
        };

        if (includeWriteScope)
            claims.Add(new Claim("scope", "quotes.write"));

        // SecurityTokenDescriptor gives full control over NotBefore and Expires,
        // which is essential for crafting expired tokens in tests.
        // For live tokens we back-date NotBefore by 1 s to absorb minor clock skew
        // between the test runner and the in-process JwtBearer middleware.
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),

            NotBefore = delta < TimeSpan.Zero
                ? now.Add(delta).AddHours(-1) // entire window is in the past
                : now.AddSeconds(-1),         // slight back-offset for clock tolerance

            Expires            = now.Add(delta),
            Issuer             = TestIssuer,
            Audience           = TestAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey)),
                SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    /// <summary>
    /// Convenience helper: returns a correctly-signed JWT whose <c>exp</c> claim
    /// is <b>one hour in the past</b>.
    ///
    /// JwtBearer's <c>ValidateLifetime = true</c> (Program.cs) will raise
    /// <see cref="Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException"/>
    /// → the <c>OnAuthenticationFailed</c> event logs it as "JWT expired"
    /// → response is <c>401 Unauthorized</c>.
    ///
    /// One hour ensures the token is well past the 5-minute default <c>ClockSkew</c>
    /// in <see cref="Microsoft.IdentityModel.Tokens.TokenValidationParameters"/>,
    /// so the rejection is deterministic regardless of runner clock precision.
    /// </summary>
    public static string CreateExpiredToken(
        string userId = "9999",
        string email  = "testonly@example.com")
    {
        var now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Email, email),
                new Claim("scope", "quotes.write")
            }),

            // Validity window is entirely in the past:
            //   NotBefore = now − 2 h   (started 2 hours ago)
            //   Expires   = now − 1 h   (expired 1 hour ago)
            NotBefore          = now.AddHours(-2),
            Expires            = now.AddHours(-1),

            Issuer             = TestIssuer,
            Audience           = TestAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestKey)),
                SecurityAlgorithms.HmacSha256)
        };

        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
