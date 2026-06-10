using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Messaging;

/// <summary>
/// Polls the OutboxMessages table for unsent rows and publishes each one to
/// Azure Service Bus, then stamps SentAtUtc.
///
/// Why this is safe:
/// • The domain write (Quote row + OutboxMessage row) is committed atomically
///   in a single EF Core transaction.  If the process crashes after the commit
///   but before the publish, SentAtUtc remains NULL, so the relay picks the row
///   up on the next poll and delivers it.
///
/// • The OutboxMessage.Id is used as the Service Bus MessageId.  Service Bus
///   topic-level duplicate detection (if enabled) and the Day-19 consumer's
///   ProcessedMessages idempotency table both key on MessageId, so receiving
///   the same event twice causes no harm.
/// </summary>
public sealed class OutboxRelayService : BackgroundService
{
    // Poll every 5 s in normal operation.  Short enough to feel responsive;
    // long enough not to hammer the database.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    // Process at most this many rows per poll cycle to bound latency spikes.
    private const int BatchSize = 20;

    private readonly IServiceScopeFactory          _scopeFactory;
    private readonly ServiceBusSender              _sender;
    private readonly ILogger<OutboxRelayService>   _logger;

    // Injected via options — allows the crash simulation endpoint to flip it.
    private readonly OutboxRelayOptions            _relayOptions;

    public OutboxRelayService(
        IServiceScopeFactory        scopeFactory,
        ServiceBusClient            client,
        ServiceBusOptions           options,
        OutboxRelayOptions          relayOptions,
        ILogger<OutboxRelayService> logger)
    {
        _scopeFactory = scopeFactory;
        _sender       = client.CreateSender(options.TopicName);
        _relayOptions = relayOptions;
        _logger       = logger;
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Outbox relay started (poll interval {Interval}s).",
            PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log and continue — a transient network error should not kill the loop.
                _logger.LogError(ex, "Outbox relay encountered an error during poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        await _sender.DisposeAsync();
        _logger.LogInformation("Outbox relay stopped.");
    }

    // ── Poll ──────────────────────────────────────────────────────────────────

    private async Task RelayBatchAsync(CancellationToken stoppingToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Fetch unsent messages in creation order so events are delivered FIFO.
        var pending = await db.OutboxMessages
            .Where(m => m.SentAtUtc == null)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(BatchSize)
            .ToListAsync(stoppingToken);

        if (pending.Count == 0)
            return;

        _logger.LogInformation("Outbox relay found {Count} pending message(s).", pending.Count);

        foreach (var outbox in pending)
        {
            // ── Crash simulation ─────────────────────────────────────────────
            // The /api/outbox/simulate-crash endpoint sets SimulateCrashBeforePublish.
            // This mimics a process dying after the DB commit but before the send.
            if (_relayOptions.SimulateCrashBeforePublish)
            {
                _logger.LogWarning(
                    "Crash simulation active — skipping publish for OutboxId={Id}. " +
                    "Restart the app (or call /api/outbox/clear-crash) to resume.",
                    outbox.Id);
                return;
            }

            await PublishOneAsync(db, outbox, stoppingToken);
        }
    }

    private async Task PublishOneAsync(
        AppDbContext     db,
        OutboxMessage    outbox,
        CancellationToken stoppingToken)
    {
        try
        {
            var message = new ServiceBusMessage(outbox.Payload)
            {
                // Stable Id → idempotent on retry.
                MessageId   = outbox.Id.ToString(),
                ContentType = "application/json",
                Subject     = outbox.EventType,
            };

            await _sender.SendMessageAsync(message, stoppingToken);

            outbox.SentAtUtc = DateTime.UtcNow;
            outbox.Error     = null;

            _logger.LogInformation(
                "Outbox relay sent OutboxId={Id} EventType={EventType} as MessageId={MessageId}.",
                outbox.Id, outbox.EventType, outbox.Id);
        }
        catch (Exception ex)
        {
            // Persist the error so it is visible in the DB.  SentAtUtc stays
            // null, so the row is retried on the next poll.
            outbox.Error = ex.Message;

            _logger.LogError(ex,
                "Outbox relay failed to publish OutboxId={Id}. Will retry.",
                outbox.Id);
        }
        finally
        {
            // Always save: either stamps SentAtUtc or records the Error.
            await db.SaveChangesAsync(stoppingToken);
        }
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Outbox relay stopping.");
        await base.StopAsync(cancellationToken);
    }
}
