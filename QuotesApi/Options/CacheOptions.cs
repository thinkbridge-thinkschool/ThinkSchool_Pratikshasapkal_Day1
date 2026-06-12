namespace QuotesApi.Options;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    // Leave empty to use the in-process DistributedMemoryCache fallback.
    // In production set to "redis-host:6379" or a full Redis connection string.
    public string RedisConnectionString { get; init; } = string.Empty;

    // L2 (distributed) TTL for single-quote lookups.
    public int QuoteByIdTtlSeconds { get; init; } = 300;

    // L2 TTL for paginated list responses.
    public int QuoteListTtlSeconds { get; init; } = 60;

    // L1 (in-process) TTL — short so the local cache never serves stale data
    // long after Redis was invalidated.
    public int LocalCacheTtlSeconds { get; init; } = 30;
}
