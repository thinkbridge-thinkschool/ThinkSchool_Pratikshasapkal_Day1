namespace QuotesApi.Messaging;

/// <summary>
/// Message payload published to the "quote-events" topic.
/// </summary>
/// <param name="EventType">Discriminator — e.g. "quote.created".</param>
/// <param name="QuoteId">Database Id of the affected quote.</param>
/// <param name="Author">Author name at the time of the event.</param>
/// <param name="Poison">
/// When true the consumer intentionally throws, triggering Service Bus retries
/// until MaxDeliveryCount is exceeded and the message lands in the DLQ.
/// </param>
public sealed record QuoteEvent(
    string EventType,
    int?   QuoteId,
    string? Author,
    bool   Poison = false);
