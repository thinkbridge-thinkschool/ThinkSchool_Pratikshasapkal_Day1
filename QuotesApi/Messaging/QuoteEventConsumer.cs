using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>
/// <para>
/// BackgroundService that starts one <see cref="ServiceBusProcessor"/> per
/// configured subscription. Each processor runs up to
/// <see cref="ServiceBusOptions.MaxConcurrentCalls"/> message handlers
/// concurrently — this is the competing-consumers pattern: multiple in-flight
/// handlers compete to process messages from the same subscription.
/// </para>
/// <para>
/// Idempotency: before processing, checks <c>ProcessedMessages</c> for the
/// (MessageId, Subscription) pair. Duplicate deliveries are completed without
/// re-executing business logic.
/// </para>
/// <para>
/// Poison messages: if <see cref="QuoteEvent.Poison"/> is <c>true</c> the
/// handler abandons the message. Service Bus retries up to MaxDeliveryCount
/// times then moves the message to the Dead Letter Queue automatically.
/// </para>
/// </summary>
public sealed class QuoteEventConsumer : BackgroundService
{
    private readonly ServiceBusClient         _client;
    private readonly ServiceBusOptions        _options;
    private readonly IServiceScopeFactory     _scopeFactory;
    private readonly ILogger<QuoteEventConsumer> _logger;

    // Kept so StopAsync can gracefully stop every processor.
    private readonly List<ServiceBusProcessor> _processors = [];

    public QuoteEventConsumer(
        ServiceBusClient            client,
        ServiceBusOptions           options,
        IServiceScopeFactory        scopeFactory,
        ILogger<QuoteEventConsumer> logger)
    {
        _client      = client;
        _options     = options;
        _scopeFactory = scopeFactory;
        _logger      = logger;
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var subscription in _options.Subscriptions)
        {
            var processor = _client.CreateProcessor(
                _options.TopicName,
                subscription,
                new ServiceBusProcessorOptions
                {
                    // Explicit Complete/Abandon gives the handler full control.
                    AutoCompleteMessages = false,
                    // >1 demonstrates competing consumers within this single process.
                    MaxConcurrentCalls   = _options.MaxConcurrentCalls,
                });

            // Capture loop variable for the closure.
            var sub = subscription;
            processor.ProcessMessageAsync += args => HandleMessageAsync(args, sub, stoppingToken);
            processor.ProcessErrorAsync   += HandleErrorAsync;

            await processor.StartProcessingAsync(stoppingToken);
            _processors.Add(processor);

            _logger.LogInformation(
                "Consumer started — topic '{Topic}', subscription '{Sub}', " +
                "MaxConcurrentCalls={Concurrency}.",
                _options.TopicName, sub, _options.MaxConcurrentCalls);
        }

        // Hold the hosted-service alive until the host requests shutdown.
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* expected on shutdown */ }
    }

    // ── Message handler ───────────────────────────────────────────────────────

    private async Task HandleMessageAsync(
        ProcessMessageEventArgs args,
        string                  subscription,
        CancellationToken       stoppingToken)
    {
        var messageId     = args.Message.MessageId;
        var deliveryCount = args.Message.DeliveryCount;

        _logger.LogInformation(
            "[{Sub}] Received MessageId={MessageId}, DeliveryCount={Count}.",
            subscription, messageId, deliveryCount);

        // ── Deserialise ──────────────────────────────────────────────────────
        QuoteEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<QuoteEvent>(args.Message.Body.ToArray());
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "[{Sub}] Deserialization failed for MessageId={MessageId}. Dead-lettering.",
                subscription, messageId);
            await args.DeadLetterMessageAsync(
                args.Message,
                deadLetterReason: "DeserializationFailed",
                deadLetterErrorDescription: ex.Message,
                cancellationToken: stoppingToken);
            return;
        }

        if (evt is null)
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                deadLetterReason: "NullPayload",
                deadLetterErrorDescription: "Deserialized to null.",
                cancellationToken: stoppingToken);
            return;
        }

        // ── Idempotency check ────────────────────────────────────────────────
        // AppDbContext is Scoped; create a scope per message so the context is
        // not shared across concurrent handlers.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var alreadyProcessed = await db.ProcessedMessages
            .AnyAsync(
                m => m.MessageId == messageId && m.Subscription == subscription,
                stoppingToken);

        if (alreadyProcessed)
        {
            _logger.LogInformation(
                "[{Sub}] MessageId={MessageId} already processed — completing (skip).",
                subscription, messageId);
            await args.CompleteMessageAsync(args.Message, stoppingToken);
            return;
        }

        // ── Poison message ───────────────────────────────────────────────────
        // Abandon → Service Bus re-enqueues and increments DeliveryCount.
        // After MaxDeliveryCount attempts the broker moves the message to the
        // Dead Letter sub-queue automatically.
        if (evt.Poison)
        {
            _logger.LogWarning(
                "[{Sub}] Poison message — MessageId={MessageId}, " +
                "DeliveryCount={Count}. Abandoning for retry/DLQ.",
                subscription, messageId, deliveryCount);

            await args.AbandonMessageAsync(args.Message, cancellationToken: stoppingToken);
            return;
        }

        // ── Normal processing ────────────────────────────────────────────────
        _logger.LogInformation(
            "[{Sub}] Processing {EventType} for QuoteId={QuoteId}.",
            subscription, evt.EventType, evt.QuoteId);

        // Simulate business work (replace with real logic as needed).
        await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);

        // Write idempotency record — the unique index prevents double-insert
        // if two concurrent handlers somehow race past the AnyAsync check.
        try
        {
            db.ProcessedMessages.Add(
                ProcessedMessage.Record(messageId, subscription, evt.EventType));
            await db.SaveChangesAsync(stoppingToken);
        }
        catch (DbUpdateException)
        {
            // Another handler beat us to it — treat as already processed.
            _logger.LogInformation(
                "[{Sub}] MessageId={MessageId} already persisted (concurrent handler).",
                subscription, messageId);
        }

        await args.CompleteMessageAsync(args.Message, stoppingToken);

        _logger.LogInformation(
            "[{Sub}] MessageId={MessageId} completed.",
            subscription, messageId);
    }

    // ── Error handler ─────────────────────────────────────────────────────────

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Service Bus processor error. Source={Source}, EntityPath={Path}.",
            args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Service Bus consumers.");

        // Stop all processors concurrently; each drains in-flight handlers
        // before returning.
        await Task.WhenAll(
            _processors.Select(p => p.StopProcessingAsync(cancellationToken)));

        await Task.WhenAll(
            _processors.Select(p => p.DisposeAsync().AsTask()));

        await base.StopAsync(cancellationToken);

        _logger.LogInformation("Service Bus consumers stopped.");
    }
}
