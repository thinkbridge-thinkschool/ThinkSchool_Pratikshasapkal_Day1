namespace QuotesApi.Options;

public sealed class OpenTelemetryOptions
{
    public const string Section = "OpenTelemetry";

    public string ServiceName { get; init; } = "QuotesApi";
}
