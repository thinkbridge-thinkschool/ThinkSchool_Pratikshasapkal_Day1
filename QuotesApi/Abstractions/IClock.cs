namespace QuotesApi.Abstractions
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get ;}
    }
}