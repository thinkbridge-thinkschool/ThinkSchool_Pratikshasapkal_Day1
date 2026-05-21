using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace QuotesApi.Tests.Helpers;

internal static class JwtTestHelper
{
    internal static string Generate(
        IConfiguration cfg,
        string email,
        bool includeScope = true,
        DateTime? expires = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "99"),
            new(ClaimTypes.Email, email)
        };

        if (includeScope)
            claims.Add(new("scope", "quotes.write"));

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));

        var token = new JwtSecurityToken(
            issuer: cfg["Jwt:Issuer"],
            audience: cfg["Jwt:Audience"],
            claims: claims,
            expires: expires ?? DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
