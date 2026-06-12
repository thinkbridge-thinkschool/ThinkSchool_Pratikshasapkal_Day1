namespace QuotesApi.Messaging;

public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>
    /// Full connection string from the namespace Shared Access Policy.
    /// Leave empty in production and use <see cref="FullyQualifiedNamespace"/>
    /// with Managed Identity instead.
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// Used instead of <see cref="ConnectionString"/> when authenticating via
    /// DefaultAzureCredential (Managed Identity in Azure Container Apps).
    /// Example: "sb-quotesapi-pratiksha.servicebus.windows.net"
    /// </summary>
    public string FullyQualifiedNamespace { get; init; } = string.Empty;

    public string TopicName { get; init; } = "quote-events";

    /// <summary>
    /// Subscription names the consumer will process.
    /// Each subscription receives every message from the topic (fan-out).
    /// </summary>
    public string[] Subscriptions { get; init; } =
        ["analytics-subscription", "audit-subscription"];

    /// <summary>
    /// Maximum concurrent message handlers per subscription processor.
    /// Setting > 1 demonstrates the competing-consumers pattern within a
    /// single process instance.
    /// </summary>
    public int MaxConcurrentCalls { get; init; } = 2;
}
