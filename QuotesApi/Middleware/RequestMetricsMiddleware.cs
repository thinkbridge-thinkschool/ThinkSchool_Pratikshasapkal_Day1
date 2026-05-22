using System.Diagnostics;
using QuotesApi.Metrics;

namespace QuotesApi.Middleware;

// Wraps every request to record duration and tag it with the route template,
// HTTP method, and response status code.
//
// Must be the outermost user-added middleware so that:
//   - The exception handler runs inside _next and sets the 500 status before
//     the finally block reads context.Response.StatusCode.
//   - The full end-to-end latency (including auth, routing, handler) is captured.
//
// Route is read from the matched RouteEndpoint after _next completes, which
// gives the template ("/api/quotes/{id}") not the concrete path — keeping
// http.route low-cardinality.
public sealed class RequestMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiMetrics _metrics;

    public RequestMetricsMiddleware(RequestDelegate next, ApiMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();

            var endpoint = context.GetEndpoint() as RouteEndpoint;
            var route = endpoint?.RoutePattern.RawText ?? "unmatched";

            _metrics.RecordRequest(
                sw.Elapsed.TotalMilliseconds,
                route,
                context.Request.Method,
                context.Response.StatusCode);
        }
    }
}
