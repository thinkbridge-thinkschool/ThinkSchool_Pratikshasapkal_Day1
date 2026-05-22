using System.Diagnostics.Metrics;

namespace QuotesApi.Metrics;

// Central metrics registry for the QuotesApi.
// Meter name "QuotesApi" is the subscription key for dotnet-counters,
// OpenTelemetry collectors, and Application Insights.
// All tag values are low-cardinality — no user IDs, quote IDs, or free-text.
public sealed class ApiMetrics
{
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _loginAttempts;
    private readonly Counter<long> _tokenRefreshes;
    private readonly Counter<long> _jwtFailures;
    private readonly Counter<long> _authorizationFailures;
    private readonly Counter<long> _quotesCreated;
    private readonly Counter<long> _quotesDeleted;

    public ApiMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("QuotesApi");

        // Follows OpenTelemetry HTTP server semantic conventions.
        // The histogram's count gives total requests; percentiles give latency distribution.
        _requestDuration = meter.CreateHistogram<double>(
            "http.server.request.duration",
            unit: "ms",
            description: "HTTP request duration in milliseconds");

        _loginAttempts = meter.CreateCounter<long>(
            "auth.login.attempts",
            description: "Login attempts by result (success | failed)");

        _tokenRefreshes = meter.CreateCounter<long>(
            "auth.token.refreshes",
            description: "Token refresh attempts by result (rotated | expired | reuse_detected | revoked | not_found)");

        _jwtFailures = meter.CreateCounter<long>(
            "auth.jwt.failures",
            description: "JWT validation failures by scheme and failure type");

        _authorizationFailures = meter.CreateCounter<long>(
            "auth.authorization.failures",
            description: "Resource-level authorization failures by resource type");

        _quotesCreated = meter.CreateCounter<long>(
            "quotes.created",
            description: "Quotes successfully created");

        _quotesDeleted = meter.CreateCounter<long>(
            "quotes.deleted",
            description: "Quotes soft-deleted");
    }

    // Tags follow OTel HTTP conventions: http.route, http.request.method, http.response.status_code.
    // http.route uses the route template ("/api/quotes/{id}"), never the actual URL path —
    // that would be high-cardinality.
    public void RecordRequest(double durationMs, string route, string method, int statusCode) =>
        _requestDuration.Record(durationMs,
            new KeyValuePair<string, object?>("http.route", route),
            new KeyValuePair<string, object?>("http.request.method", method),
            new KeyValuePair<string, object?>("http.response.status_code", statusCode));

    // result: "success" | "failed"
    public void RecordLogin(string result) =>
        _loginAttempts.Add(1, new KeyValuePair<string, object?>("result", result));

    // result: "rotated" | "expired" | "reuse_detected" | "revoked" | "not_found"
    public void RecordTokenRefresh(string result) =>
        _tokenRefreshes.Add(1, new KeyValuePair<string, object?>("result", result));

    // scheme: "bearer" | "entra"   failure_type: "expired" | "invalid"
    public void RecordJwtFailure(string scheme, string failureType) =>
        _jwtFailures.Add(1,
            new KeyValuePair<string, object?>("scheme", scheme),
            new KeyValuePair<string, object?>("failure_type", failureType));

    // resource: low-cardinality type name, e.g. "quote"
    public void RecordAuthorizationFailure(string resource) =>
        _authorizationFailures.Add(1, new KeyValuePair<string, object?>("resource", resource));

    public void RecordQuoteCreated() => _quotesCreated.Add(1);

    public void RecordQuoteDeleted() => _quotesDeleted.Add(1);
}
