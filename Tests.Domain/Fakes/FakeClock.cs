using QuotesApi.Abstractions;

namespace Tests.Domain.Fakes;

public class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } =
        new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
