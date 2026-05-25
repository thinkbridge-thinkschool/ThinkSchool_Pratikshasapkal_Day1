using System.Diagnostics;
using Serilog.Context;

namespace QuotesApi.Middleware;

// Why correlation IDs matter:
//   Every log line emitted during a request shares the same TraceId.
//   When something goes wrong you can filter your log tool by TraceId and see the
//   full sequence: authentication → business logic → response — across every logger,
//   every middleware, and every service involved in that single request.
//   Without it you get a wall of interleaved log lines with no way to group them.
//
// TraceId alignment with OpenTelemetry:
//   Activity.Current is the active OTel span, created by the ASP.NET Core hosting
//   layer before any user middleware runs. Reading its TraceId here ensures that
//   Serilog log lines and OTel distributed traces share the same 32-char hex ID, so
//   both appear under the same trace when you open Jaeger, Tempo, or Honeycomb.
//
//   Priority:  OTel Activity.TraceId  →  X-Correlation-ID header  →  new GUID
//   The header path handles upstream services that propagate their own correlation
//   IDs without using W3C traceparent (e.g. legacy clients).
public sealed class CorrelationIdMiddleware
{
    private const string Header = "X-Correlation-ID";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId =
            Activity.Current?.TraceId.ToHexString()          // W3C trace from OTel span
            ?? context.Request.Headers[Header].FirstOrDefault() // upstream correlation header
            ?? Guid.NewGuid().ToString("N");                    // fallback: generate

        context.Response.Headers[Header] = correlationId;
        context.Items[Header] = correlationId;

        // PushProperty flows through every await via AsyncLocal<T>.
        // All loggers fired during this request automatically include TraceId.
        using (LogContext.PushProperty("TraceId", correlationId))
        {
            _logger.LogInformation(
                "Request received Method={Method} Path={Path}",
                context.Request.Method,
                context.Request.Path.Value);

            await _next(context);
        }
    }
}
