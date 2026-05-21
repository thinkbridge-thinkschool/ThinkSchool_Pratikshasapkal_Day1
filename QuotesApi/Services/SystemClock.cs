using QuotesApi.Abstractions;
namespace QuotesApi.Services;


public class UtcSystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
