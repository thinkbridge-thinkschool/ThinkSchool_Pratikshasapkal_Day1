namespace QuotesApi.Models;

/// <summary>
/// Idempotency record written after a Service Bus message is successfully processed.
/// The unique index on (MessageId, Subscription) prevents double-processing
/// when Service Bus redelivers a message after a transient failure.
/// </summary>
public class ProcessedMessage
{
    public int Id { get; set; }

    /// <summary>Service Bus message identifier (set by the publisher).</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>Subscription name — included so the same MessageId can exist once
    /// per subscription (each subscription gets its own fan-out copy).</summary>
    public string Subscription { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; }

    public static ProcessedMessage Record(
        string messageId, string subscription, string eventType) => new()
    {
        MessageId   = messageId,
        Subscription = subscription,
        EventType   = eventType,
        ProcessedAt = DateTime.UtcNow,
    };
}
