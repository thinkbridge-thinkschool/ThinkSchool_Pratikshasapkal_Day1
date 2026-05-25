using System.Diagnostics;

namespace QuotesApi.Telemetry;

// Single ActivitySource for all custom application spans.
// The Name constant is used both here and in AddSource() so the OTel SDK
// knows to export spans from this source to the configured exporter.
//
// Spans created with this source automatically become child spans of
// Activity.Current (the ASP.NET Core request span), giving you a tree:
//
//   GET /api/quotes  (AspNetCore instrumentation)
//     └── quotes.list  (this source)
//           └── db.query  (EF Core instrumentation)
public static class AppActivitySource
{
    public const string Name = "QuotesApi";

    // ActivitySource is thread-safe and designed to be a long-lived singleton.
    public static readonly ActivitySource Instance = new(Name);
}
