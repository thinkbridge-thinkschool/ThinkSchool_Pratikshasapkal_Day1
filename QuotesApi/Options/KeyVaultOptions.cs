namespace QuotesApi.Options;

public sealed class KeyVaultOptions
{
    public const string Section = "KeyVault";

    public string Uri { get; init; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Uri);
}
