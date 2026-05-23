using System.ComponentModel.DataAnnotations;

namespace QuotesApi.Options;

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>
    /// HMAC-SHA256 signing key. Must be at least 32 characters.
    /// Never put a real value in appsettings.json — supply via user-secrets (dev)
    /// or the Jwt__Key environment variable (production).
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Jwt:Key is required. Set it via user-secrets or the Jwt__Key environment variable.")]
    [MinLength(32, ErrorMessage = "Jwt:Key must be at least 32 characters (required for HMAC-SHA256).")]
    public string Key { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; init; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = string.Empty;

    [Range(1, 1440, ErrorMessage = "Jwt:ExpiryMinutes must be between 1 and 1440.")]
    public int ExpiryMinutes { get; init; } = 15;

    [Range(1, 365, ErrorMessage = "Jwt:RefreshExpiryDays must be between 1 and 365.")]
    public int RefreshExpiryDays { get; init; } = 7;

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(ExpiryMinutes);
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshExpiryDays);
}
