using QuotesApi.Abstractions;
namespace QuotesApi.Services;


public class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
