using System.Threading.Channels;

namespace QuotesApi.Services;

/// <summary>
/// Bounded, single-reader channel. Capacity limits memory usage:
/// writers block (back-pressure) instead of unbounded growth.
/// </summary>
public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    // A bounded channel lets the runtime apply back-pressure: when the channel is
    // full, EnqueueAsync suspends the caller rather than growing the queue without
    // limit. Capacity=100 is illustrative; tune for your workload.
    private readonly Channel<Func<CancellationToken, ValueTask>> _channel;

    public BackgroundTaskQueue(int capacity = 100)
    {
        var options = new BoundedChannelOptions(capacity)
        {
            // Only one hosted service drains the channel.
            SingleReader = true,
            // Many HTTP requests may enqueue concurrently.
            SingleWriter = false,
            // Suspend the writer when full (back-pressure) rather than
            // dropping items or throwing immediately.
            FullMode = BoundedChannelFullMode.Wait,
        };

        _channel = Channel.CreateBounded<Func<CancellationToken, ValueTask>>(options);
    }

    /// <inheritdoc/>
    public async ValueTask EnqueueAsync(
        Func<CancellationToken, ValueTask> workItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        // WriteAsync suspends if the channel is full and resumes when space opens.
        // Throws ChannelClosedException (wrapped as InvalidOperationException) once
        // the channel is marked complete — i.e., after graceful shutdown begins.
        await _channel.Writer.WriteAsync(workItem, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<Func<CancellationToken, ValueTask>?> DequeueAsync(
        CancellationToken cancellationToken)
    {
        // ReadAsync blocks until an item arrives, the token is cancelled, or the
        // channel is completed (all writers finished). OperationCanceledException
        // bubbles to StopAsync in the hosted service.
        if (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_channel.Reader.TryRead(out var workItem))
                return workItem;
        }

        return null;
    }

    /// <summary>
    /// Signals that no more items will be written.
    /// Called once by QueuedHostedService during graceful shutdown.
    /// </summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
