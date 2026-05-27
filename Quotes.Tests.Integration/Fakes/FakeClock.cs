namespace Quotes.Tests.Integration.Fakes;

/// <summary>
/// Deterministic clock for integration tests.
/// UtcNow is fixed at construction time and can be advanced or set explicitly.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; }

    public FakeClock()
        : this(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero)) { }

    public FakeClock(DateTimeOffset startTime) => UtcNow = startTime;

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    public void Set(DateTimeOffset time) => UtcNow = time;
}
