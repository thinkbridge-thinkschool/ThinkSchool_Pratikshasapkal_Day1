namespace QuotesApi.Services;

/// <summary>
/// Abstraction over the bounded channel used by QueuedHostedService.
/// Callers enqueue work items without knowing the concurrency implementation.
/// </summary>
public interface IBackgroundTaskQueue
{
    /// <summary>
    /// Adds a work item to the queue.
    /// Throws <see cref="InvalidOperationException"/> after the channel is closed
    /// (i.e., after the host begins shutting down).
    /// </summary>
    ValueTask EnqueueAsync(Func<CancellationToken, ValueTask> workItem,
                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks asynchronously until a work item is available or the channel is closed.
    /// Returns false when no more items will ever arrive (host shutting down).
    /// </summary>
    ValueTask<Func<CancellationToken, ValueTask>?> DequeueAsync(
        CancellationToken cancellationToken);
}
