namespace QuotesApi.Models;

/// <summary>
/// Durable record of a domain event that must be delivered to the message broker.
/// Written atomically with the domain change that produced it (same EF Core
/// transaction), so the event is never silently dropped even if the process
/// crashes between the database commit and the broker send.
/// </summary>
public class OutboxMessage
{
    /// <summary>
    /// Stable identifier reused as the Service Bus MessageId.
    /// Because MessageId is set by the relay (not generated fresh each time),
    /// Service Bus duplicate-detection and the Day-19 consumer's idempotency
    /// table guarantee at-most-once processing even when the relay delivers
    /// the same row more than once.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;

    /// <summary>JSON-serialised event payload (e.g. a QuoteEvent record).</summary>
    public string Payload { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Null until the relay successfully sends the message to Service Bus.
    /// The relay queries WHERE SentAtUtc IS NULL to find pending rows.
    /// </summary>
    public DateTime? SentAtUtc { get; set; }

    /// <summary>
    /// Last error message if publishing failed. Informational only —
    /// a non-null Error does not stop retry; SentAtUtc IS NULL is the
    /// only signal the relay acts on.
    /// </summary>
    public string? Error { get; set; }
}
