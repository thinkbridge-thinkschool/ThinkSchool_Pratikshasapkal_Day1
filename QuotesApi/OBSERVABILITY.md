# Observability — QuotesApi

## Logs vs Traces

| | Logs (Serilog) | Traces (OpenTelemetry) |
|---|---|---|
| Unit | A single structured event | A tree of timed spans |
| Answers | "What happened, and with what data?" | "How long, what called what?" |
| Storage | Log sink (Seq, Loki, CloudWatch) | Trace backend (Jaeger, Tempo, Honeycomb) |

**Correlation**: `CorrelationIdMiddleware` copies `Activity.Current.TraceId` into Serilog's
`LogContext` before any handler runs. Every log line and every span from the same request share
the same 32-char hex `TraceId`, so one search in either tool surfaces both.

---

## OpenTelemetry Setup

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: builder.Configuration["OpenTelemetry:ServiceName"] ?? "QuotesApi",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(tracing => tracing
        .AddSource(AppActivitySource.Name)          // custom business spans
        .AddAspNetCoreInstrumentation(opts =>
        {
            opts.RecordException = true;
        })
        .AddEntityFrameworkCoreInstrumentation()    // child span per EF query
        .AddHttpClientInstrumentation()             // child span for Entra calls
        .AddOtlpExporter());
```

Packages (NuGet):

```
OpenTelemetry.Extensions.Hosting
OpenTelemetry.Instrumentation.AspNetCore
OpenTelemetry.Instrumentation.EntityFrameworkCore
OpenTelemetry.Instrumentation.Http
OpenTelemetry.Exporter.OpenTelemetryProtocol
```

Collector is configured entirely via environment variables — no code change needed:

```
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317   # Jaeger / Grafana Tempo / Honeycomb
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_SERVICE_NAME=QuotesApi
```

---

## Automatic vs Custom Instrumentation

**Automatic** — SDK hooks into the framework at startup; zero application code:

| Instrumentation | What it creates |
|---|---|
| `AddAspNetCoreInstrumentation` | One root span per incoming HTTP request |
| `AddEntityFrameworkCoreInstrumentation` | One child span per EF Core query |
| `AddHttpClientInstrumentation` | One child span per outbound HTTP call (Entra OIDC) |

**Custom** — `AppActivitySource` spans in endpoint handlers; captures business semantics
the frameworks cannot observe:

```csharp
// Telemetry/AppActivitySource.cs
public static class AppActivitySource
{
    public const string Name = "QuotesApi";
    public static readonly ActivitySource Instance = new(Name);
}
```

Usage pattern:

```csharp
// StartActivity returns null when no collector is listening.
// ?. makes every tag call a no-op — zero overhead when tracing is disabled.
// The using block records the span end time when the handler exits.
using var activity = AppActivitySource.Instance.StartActivity("quote.create");
activity?.SetTag("user.id", userId);
activity?.SetTag("quote.author", request.Author);

// ... business logic ...

activity?.SetTag("quote.id", result.Value!.Id);           // set after DB roundtrip
activity?.SetStatus(ActivityStatusCode.Error, reason);    // failure paths only
```

`.AddSource(AppActivitySource.Name)` in the tracer builder registers the source so
spans are exported. Without this call the spans are created but silently dropped.

---

## Custom Spans Inventory

| Span | Endpoint | Tags |
|---|---|---|
| `quote.create` | `POST /api/quotes` | `user.id`, `quote.author`, `quote.id`; Error status on validation failure |
| `quotes.list` | `GET /api/quotes` | `user.id`, `page`, `page.size`, `result.count` |
| `quote.delete` | `DELETE /api/quotes/{id}` | `quote.id`, `user.id`, `authz.result` (allowed/denied) |
| `authz.quote_delete` | `DeleteOwnQuoteHandler` | `quote.id`, `authz.result` |
| `auth.login` | `POST /api/auth/login` | `auth.result`, `user.id` (success only) |
| `token.rotate` | `POST /api/auth/refresh` | `result`, `user.id`, `token.family_id` |

No sensitive data in any tag (no emails, passwords, or token values).

---

## Expected Trace Trees

`POST /api/quotes` (happy path):

```
POST /api/quotes  ← AspNetCore instrumentation (root span, ~15 ms)
└── quote.create  ← custom span (AppActivitySource, ~12 ms)
      └── INSERT INTO Quotes  ← EF Core instrumentation (~3 ms)
```

`DELETE /api/quotes/{id}` with ownership check:

```
DELETE /api/quotes/{id}  ← AspNetCore (~20 ms)
└── quote.delete  ← custom (tags: quote.id, user.id, authz.result)
      ├── SELECT FROM Quotes  ← EF Core (fetch for ownership check)
      ├── authz.quote_delete  ← custom (DeleteOwnQuoteHandler)
      └── UPDATE Quotes  ← EF Core (soft-delete save)
```

`GET /api/quotes` with Entra token (token validation triggers an OIDC metadata fetch):

```
GET /api/quotes  ← AspNetCore
├── GET login.microsoftonline.com/…/openid-configuration  ← HttpClient (Entra OIDC)
└── quotes.list  ← custom (tags: user.id, page, page.size, result.count)
      └── SELECT FROM Quotes  ← EF Core
```

`POST /api/auth/refresh` — reuse attack detected:

```
POST /api/auth/refresh  ← AspNetCore
└── token.rotate  ← custom (tags: result=reuse_detected, user.id, token.family_id)
      ├── SELECT FROM RefreshTokens (fetch presented token)
      └── UPDATE RefreshTokens (revoke family — multiple rows)  ← EF Core
```

---

## Sample Correlated Trace + Log

Both records below come from the same `POST /api/quotes` request.

**Serilog log line** (JSON sink):

```json
{
  "Timestamp": "2025-05-22T14:03:21.441Z",
  "Level": "Information",
  "MessageTemplate": "Quote created QuoteId={QuoteId} Author={Author} CreatedBy={UserEmail}",
  "Properties": {
    "QuoteId": 7,
    "Author": "Feynman",
    "UserEmail": "admin@example.com",
    "TraceId": "4bf92f3577b34da6a3ce929d0e0e4736"
  }
}
```

**OTel span** (as seen in Jaeger):

```
Span:     quote.create
TraceId:  4bf92f3577b34da6a3ce929d0e0e4736   ← identical to log line above
SpanId:   00f067aa0ba902b7
Duration: 12 ms
Tags:
  user.id      = 1
  quote.author = Feynman
  quote.id     = 7
```

Filter either tool by `TraceId = 4bf92f3577b34da6a3ce929d0e0e4736` to see every
log event and every span from that single request in one view.

---

## Running a Local Collector (Jaeger all-in-one)

```bash
docker run -d --name jaeger \
  -p 4317:4317 \
  -p 16686:16686 \
  jaegertracing/all-in-one:latest
```

Start the API with the collector endpoint:

```bash
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project day-1/QuotesApi
```

Open Jaeger UI at `http://localhost:16686` and select service `QuotesApi`.
No code change is needed — `AddOtlpExporter()` reads the env var automatically.

---

## Azure Monitor / Application Insights (Production)

### Required Azure resources

| Resource | Purpose |
|---|---|
| Application Insights | Receives traces, metrics, and logs |
| Log Analytics Workspace | Backend store for App Insights |
| Key Vault | Holds the connection string secret |
| Managed Identity | Grants the app read access to Key Vault |

### Key Vault secret

Create one secret in Key Vault with exactly this name:

```
appinsights-connection-string
```

Value: the full Application Insights connection string, e.g.
`InstrumentationKey=…;IngestionEndpoint=https://…;LiveEndpoint=https://…`

Grant the app's Managed Identity the **Key Vault Secrets User** role on the vault.

### Configuration

Set `KeyVault:Uri` to your vault URL — **never the connection string itself**:

```json
// appsettings.Production.json  (NOT committed — listed in .gitignore)
{
  "KeyVault": {
    "Uri": "https://your-vault.vault.azure.net/"
  }
}
```

**Local development fallback** (no Key Vault required):

```powershell
$env:APPLICATIONINSIGHTS_CONNECTION_STRING = "InstrumentationKey=…"
dotnet run --project day-1/QuotesApi
```

Run `az login` once so `DefaultAzureCredential` can use `AzureCliCredential`
when `KeyVault:Uri` is set locally.

### What gets exported to App Insights

| Signal | Source | App Insights table |
|---|---|---|
| Distributed traces | OTel + custom spans | `dependencies`, `requests` |
| Metrics | `ApiMetrics` (System.Diagnostics.Metrics) | `customMetrics` |
| Logs | `ILogger<T>` → OTel log pipeline | `traces` |

> **Note on Serilog logs**: `UseSerilog()` replaces the `ILogger` provider pipeline,
> so OTel's log exporter does not receive Serilog output directly. For full log
> ingestion in production, either: (a) add `Serilog.Sinks.OpenTelemetry` and point
> it at the OTLP endpoint, or (b) use `Serilog.Sinks.ApplicationInsights` alongside
> the connection string from Key Vault.

### TraceId correlation across all three signals

```
User request
  │
  ├── OTel root span  (AspNetCore instrumentation)
  │     TraceId = 4bf92f3577b34da6a3ce929d0e0e4736
  │
  ├── CorrelationIdMiddleware pushes TraceId into Serilog LogContext
  │     → every log line: { "TraceId": "4bf92f3577b34da6a3ce929d0e0e4736" }
  │
  └── Azure Monitor exporter ships both to App Insights
        → search App Insights by operation_Id = "4bf92f3577b34da6a3ce929d0e0e4736"
          to see logs + traces for one request in a single view
```

`operation_Id` in App Insights maps to the W3C `traceId`. The Serilog `TraceId`
property is stored in `customDimensions.TraceId` and holds the same value.

---

## KQL Queries

### Traces for a specific user (last 15 minutes)

```kql
traces
| where timestamp > ago(15m)
| where customDimensions.UserId == "1"
| order by timestamp asc
```

### All spans for a single request (by TraceId)

```kql
union traces, requests, dependencies
| where operation_Id == "4bf92f3577b34da6a3ce929d0e0e4736"
| order by timestamp asc
| project timestamp, itemType, name, duration, success, customDimensions
```

### Failed logins in the last hour

```kql
traces
| where timestamp > ago(1h)
| where message has "Login failed"
| summarize count() by bin(timestamp, 5m)
| render timechart
```

### Token reuse attacks detected

```kql
traces
| where timestamp > ago(24h)
| where customDimensions["token.rotate.result"] == "reuse_detected"
   or message has "reuse detected"
| project timestamp, operation_Id, customDimensions
```

### Slow quote creation (p95 latency by hour)

```kql
dependencies
| where timestamp > ago(24h)
| where name == "quote.create"
| summarize p50=percentile(duration, 50), p95=percentile(duration, 95) by bin(timestamp, 1h)
| render timechart
```

### EF Core query hotspots

```kql
dependencies
| where timestamp > ago(1h)
| where type == "sqlite" or type == "SQL"
| top 20 by duration desc
| project timestamp, name, duration, data, operation_Id
```

---

## Azure Monitor Alerts

### Alert: POST /api/quotes slow (> 500 ms avg over 5 min)

**Condition** (Metric alert on Application Insights):

| Field | Value |
|---|---|
| Signal | `requests/duration` |
| Filter | `request/name == POST /api/quotes` |
| Aggregation | Average |
| Threshold | > 500 ms |
| Evaluation window | 5 minutes |
| Frequency | 1 minute |

**Action group**: create an action group in Azure Monitor with:
- Action type: Email/SMS/Push/Voice
- Email: `oncall@your-domain.com`
- Name: `quotes-oncall`

Attach the action group to the alert rule. ARM template fragment:

```json
{
  "type": "Microsoft.Insights/scheduledQueryRules",
  "properties": {
    "severity": 2,
    "evaluationFrequency": "PT1M",
    "windowSize": "PT5M",
    "criteria": {
      "allOf": [{
        "query": "requests | where name == 'POST /api/quotes' | summarize avg(duration)",
        "threshold": 500,
        "operator": "GreaterThan",
        "timeAggregation": "Average"
      }]
    },
    "actions": { "actionGroups": ["<action-group-resource-id>"] }
  }
}
```

### Alert: authentication failures spike

```kql
// Fires when > 10 failed logins in 5 minutes
traces
| where timestamp > ago(5m)
| where message has "Login failed"
| count
```

Threshold: `> 10`, window: 5 min, severity: 1 (Critical).

### Production alerting guidance

- **Severity 1** (Critical): token reuse detected, auth failure spike, 5xx error rate > 1%
- **Severity 2** (Warning): p95 response time > 500 ms, EF query > 200 ms avg
- **Severity 3** (Info): login rate anomaly, unusual quote-creation volume
- Always attach alerts to an action group; avoid per-alert email configuration
- Use **dynamic thresholds** for rate-based metrics (login/min) to handle traffic variation

---

## Commit Hash

| Commit | Description |
|---|---|
| `80e1dcc` | feat: add custom OTel spans for business operations |
| `15e78af` | docs: add observability documentation and expand OTel inline comments |
