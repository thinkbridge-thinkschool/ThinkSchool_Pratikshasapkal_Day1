using System.Diagnostics.Metrics;

namespace QuotesApi.Metrics;

// Cache observability metrics — shares the "QuotesApi" meter so all counters
// appear together in a single dotnet-counters or OTel collector view.
// All tag values are low-cardinality operation names ("quote_by_id", "quote_list").
public sealed class CacheMetrics
{
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;
    private readonly Counter<long> _dbQueries;
    private readonly Histogram<double> _dbQueryDuration;

    public CacheMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("QuotesApi");

        _cacheHits = meter.CreateCounter<long>(
            "cache.hits",
            description: "Cache hits served without a database round-trip");

        // Each miss corresponds to exactly one DB query because HybridCache
        // coalesces concurrent misses (stampede protection).
        _cacheMisses = meter.CreateCounter<long>(
            "cache.misses",
            description: "Cache misses that triggered a database fetch");

        _dbQueries = meter.CreateCounter<long>(
            "db.queries",
            description: "Database queries executed on behalf of cache-miss fetches");

        _dbQueryDuration = meter.CreateHistogram<double>(
            "db.query.duration",
            unit: "ms",
            description: "Execution time of database queries triggered by cache misses");
    }

    // operation: "quote_by_id" | "quote_list"
    public void RecordHit(string operation) =>
        _cacheHits.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void RecordMiss(string operation) =>
        _cacheMisses.Add(1, new KeyValuePair<string, object?>("operation", operation));

    public void RecordDbQuery(string operation, double durationMs)
    {
        _dbQueries.Add(1, new KeyValuePair<string, object?>("operation", operation));
        _dbQueryDuration.Record(durationMs, new KeyValuePair<string, object?>("operation", operation));
    }
}
