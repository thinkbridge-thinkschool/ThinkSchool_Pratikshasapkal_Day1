using Azure.Messaging.ServiceBus;
using System.Text.Json;

namespace QuotesApi.Messaging;

/// <summary>
/// Singleton publisher.  Holds a single <see cref="ServiceBusSender"/> for
/// the lifetime of the application and disposes it cleanly on shutdown.
/// </summary>
public sealed class ServiceBusPublisher : IMessagePublisher, IAsyncDisposable
{
    private readonly ServiceBusSender           _sender;
    private readonly ILogger<ServiceBusPublisher> _logger;

    public ServiceBusPublisher(
        ServiceBusClient              client,
        ServiceBusOptions             options,
        ILogger<ServiceBusPublisher>  logger)
    {
        _sender = client.CreateSender(options.TopicName);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task PublishAsync(
        QuoteEvent         evt,
        CancellationToken  cancellationToken = default)
    {
        // A new random GUID for each message is the idempotency key.
        // The consumer stores it in ProcessedMessages so duplicate deliveries
        // are recognised and skipped.
        var messageId = Guid.NewGuid().ToString();

        var body = JsonSerializer.SerializeToUtf8Bytes(evt);

        var message = new ServiceBusMessage(body)
        {
            MessageId   = messageId,
            ContentType = "application/json",
            // Subject surfaces in Service Bus Explorer / DLQ diagnostics
            // without having to decode the body.
            Subject     = evt.EventType,
        };

        await _sender.SendMessageAsync(message, cancellationToken);

        _logger.LogInformation(
            "Published {EventType} MessageId={MessageId} Poison={Poison}.",
            evt.EventType, messageId, evt.Poison);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
    }
}
