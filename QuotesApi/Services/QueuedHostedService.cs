using Microsoft.Extensions.Hosting;

namespace QuotesApi.Services;

/// <summary>
/// Long-running singleton that drains <see cref="IBackgroundTaskQueue"/> on a
/// dedicated background thread.  The host starts it automatically via
/// <c>IHostedService</c> and stops it when the application shuts down.
/// </summary>
public sealed class QueuedHostedService : BackgroundService
{
    private readonly BackgroundTaskQueue _queue;
    private readonly ILogger<QueuedHostedService> _logger;

    // BackgroundService gives us a CancellationToken wired to IHostApplicationLifetime.
    // When the host calls StopAsync(), it cancels _stoppingToken which is passed into
    // ExecuteAsync as the stoppingToken parameter.

    public QueuedHostedService(
        IBackgroundTaskQueue queue,
        ILogger<QueuedHostedService> logger)
    {
        // Downcast so we can call Complete() — only the service itself should do this.
        _queue  = (BackgroundTaskQueue)queue;
        _logger = logger;
    }

    // ── Startup ─────────────────────────────────────────────────────────────

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Queued hosted service starting.");
        return base.StartAsync(cancellationToken);
    }

    // ── Main loop ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once by <see cref="BackgroundService"/> on a ThreadPool thread.
    /// Runs until <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Queued hosted service is running.");

        // The loop exits when stoppingToken fires (graceful shutdown) or the
        // channel is drained and completed.
        await ProcessQueueAsync(stoppingToken);
    }

    private async Task ProcessQueueAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Func<CancellationToken, ValueTask>? workItem;

            try
            {
                // DequeueAsync suspends here until an item arrives or shutdown starts.
                workItem = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // stoppingToken was cancelled — exit cleanly.
                break;
            }

            if (workItem is null)
                break; // channel completed with no more items

            try
            {
                // Pass stoppingToken so the work item itself can respond to shutdown.
                await workItem(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Background work item cancelled because the host is shutting down.");
            }
            catch (Exception ex)
            {
                // Never crash the service loop on a work-item failure.
                _logger.LogError(ex,
                    "Unhandled exception in background work item.");
            }
        }
    }

    // ── Shutdown ─────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Queued hosted service is stopping. " +
            "Remaining items in the queue will not be executed.");

        // 1. Tell the channel no more work will be written.
        //    Any writer that is currently awaiting space gets ChannelClosedException.
        _queue.Complete();

        // 2. Let BackgroundService cancel stoppingToken and await ExecuteAsync to finish.
        await base.StopAsync(cancellationToken);

        _logger.LogInformation("Queued hosted service stopped.");
    }
}
