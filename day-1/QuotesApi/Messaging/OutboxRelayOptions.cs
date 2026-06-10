namespace QuotesApi.Messaging;

/// <summary>
/// Mutable singleton toggled by the crash-simulation endpoints.
/// Volatile so the relay loop always reads the current value without
/// the compiler caching it in a register.
/// </summary>
public sealed class OutboxRelayOptions
{
    private volatile bool _simulateCrash;

    /// <summary>
    /// When true the relay skips the Service Bus send, simulating a crash
    /// between the DB commit and the broker publish.
    /// </summary>
    public bool SimulateCrashBeforePublish
    {
        get => _simulateCrash;
        set => _simulateCrash = value;
    }
}
