namespace QuotesApi.Messaging;

public interface IMessagePublisher
{
    /// <summary>
    /// Serialises <paramref name="evt"/> to JSON and sends it to the configured
    /// Service Bus topic with a generated <c>MessageId</c>.
    /// </summary>
    Task PublishAsync(QuoteEvent evt, CancellationToken cancellationToken = default);
}
