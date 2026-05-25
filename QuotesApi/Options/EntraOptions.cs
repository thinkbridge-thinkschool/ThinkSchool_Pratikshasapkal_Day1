namespace QuotesApi.Options;

/// <summary>
/// Azure AD (Entra ID) token validation settings.
/// All three fields are required together; leave all empty to disable Entra auth.
/// Non-sensitive public identifiers — safe in appsettings.json.
/// </summary>
public sealed class EntraOptions
{
    public const string Section = "Entra";

    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string Audience { get; init; } = string.Empty;

    /// <summary>True when all three Entra fields are populated.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(Audience);

    /// <summary>OIDC authority URL derived from TenantId.</summary>
    public string Authority =>
        $"https://login.microsoftonline.com/{TenantId}/v2.0";
}
