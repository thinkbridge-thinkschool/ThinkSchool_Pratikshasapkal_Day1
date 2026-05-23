# Performance Diagnosis — N+1 Query in GET /api/quotes

## Summary

| | Before fix | After fix |
|---|---|---|
| Endpoint | `GET /api/quotes` | `GET /api/quotes` |
| DB round-trips (page=10) | **11** | **2** |
| Total request duration | ~120–400 ms | ~8–25 ms |
| EF Core spans in Jaeger | **11** child spans | **2** child spans |
| Span tag `queries.issued` | `10` | `1` |
| Span tag `strategy` | `n+1` | `batched` |

---

## Root Cause

### What is an N+1 query?

An N+1 query occurs when code fetches a list of N items (query #1) and then issues an
additional query for each item in that list (queries #2 through #N+1).

### The specific N+1 here

The endpoint added a `CollectionCount` field to every quote response — how many
`CollectionItem` rows across all collections reference that quote. The naive
implementation issued a separate `SELECT COUNT(*)` inside a `foreach` loop:

```csharp
// BEFORE — N+1
foreach (var q in quotes)            // quotes is the paged result (up to 10 items)
{
    var collectionCount = await db.Collections
        .SelectMany(c => c.Items)
        .CountAsync(i => i.QuoteId == q.Id, cancellationToken);
    //                                ^^^^ one SQL round-trip per quote
    ...
}
```

With a page of 10 quotes this produces **11 DB round-trips**:

```
SELECT * FROM Quotes WHERE IsDeleted=0 ORDER BY Id LIMIT 10 OFFSET 0   ← query 1
SELECT COUNT(*) FROM CollectionItem WHERE QuoteId = 1                   ← query 2
SELECT COUNT(*) FROM CollectionItem WHERE QuoteId = 2                   ← query 3
SELECT COUNT(*) FROM CollectionItem WHERE QuoteId = 3                   ← query 4
...
SELECT COUNT(*) FROM CollectionItem WHERE QuoteId = 10                  ← query 11
```

---

## How it appeared in traces

### Jaeger span tree — before fix

```
GET /api/quotes                              ~380 ms  ← AspNetCore root span
└── quotes.list                             ~370 ms  ← custom span
      ├── SELECT Quotes (paged)              ~12 ms  ← EF Core span
      └── quotes.collection-counts          ~355 ms  ← custom span
            ├── SELECT COUNT(*) QuoteId=1    ~35 ms  ← EF Core span (×10)
            ├── SELECT COUNT(*) QuoteId=2    ~34 ms
            ├── SELECT COUNT(*) QuoteId=3    ~33 ms
            ├── SELECT COUNT(*) QuoteId=4    ~35 ms
            ├── SELECT COUNT(*) QuoteId=5    ~36 ms
            ├── SELECT COUNT(*) QuoteId=6    ~34 ms
            ├── SELECT COUNT(*) QuoteId=7    ~35 ms
            ├── SELECT COUNT(*) QuoteId=8    ~34 ms
            ├── SELECT COUNT(*) QuoteId=9    ~35 ms
            └── SELECT COUNT(*) QuoteId=10   ~34 ms
```

**Key signal**: a "staircase" of 10 identical child spans with sequential, non-overlapping
start times. Sequential = synchronous loop. Identical query shape = N+1.

Custom span tags on `quotes.collection-counts`:
```
strategy     = n+1
quote.count  = 10
queries.issued = 10
```

### Serilog log entry — before fix (correlates via TraceId)

```json
{
  "Level": "Warning",
  "MessageTemplate": "N+1 pattern: issuing {ExtraQueries} extra DB round-trips for {QuoteCount} quotes — TraceId={TraceId}",
  "Properties": {
    "ExtraQueries": 10,
    "QuoteCount": 10,
    "TraceId": "4bf92f3577b34da6a3ce929d0e0e4736"
  }
}
```

The `TraceId` property matches the OTel `traceId` of the slow span in Jaeger.
Filter Jaeger by that trace ID to see the full staircase in one view.

---

## The Fix

Replace the N separate `CountAsync` calls with a single `SelectMany` + `GroupBy` query:

```csharp
// AFTER — batched (2 total DB round-trips regardless of page size)
var quoteIds = quotes.Select(q => q.Id).ToList();

var collectionCounts = await db.Collections
    .SelectMany(c => c.Items)
    .Where(i => quoteIds.Contains(i.QuoteId))     // IN (1, 2, 3, …, 10)
    .GroupBy(i => i.QuoteId)
    .Select(g => new { QuoteId = g.Key, Count = g.Count() })
    .ToDictionaryAsync(x => x.QuoteId, x => x.Count, cancellationToken);
```

Translated SQL (one query):
```sql
SELECT i.QuoteId, COUNT(*) AS Count
FROM   Collections c
JOIN   CollectionItem i ON i.CollectionId = c.Id
WHERE  i.QuoteId IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10)
GROUP  BY i.QuoteId
```

The result dictionary is joined in-memory (O(N)) to produce the final response —
no further DB calls.

---

## Jaeger span tree — after fix

```
GET /api/quotes                              ~18 ms  ← AspNetCore root span
└── quotes.list                              ~15 ms  ← custom span
      ├── SELECT Quotes (paged)               ~8 ms  ← EF Core span
      └── quotes.collection-counts            ~5 ms  ← custom span
            └── SELECT ... GROUP BY QuoteId   ~4 ms  ← EF Core span (×1)
```

Custom span tags on `quotes.collection-counts`:
```
strategy       = batched
quote.count    = 10
queries.issued = 1
```

The staircase is gone. Two child spans instead of eleven. Total duration drops
from ~380 ms to ~18 ms — a **~95 % reduction** for a page of 10 quotes.
The reduction is proportional to page size: a page of 50 would have shown 51→2 round-trips.

---

## How to reproduce locally

### Start Jaeger all-in-one

```powershell
docker run -d --name jaeger `
  -p 4317:4317 `
  -p 16686:16686 `
  jaegertracing/all-in-one:latest
```

### Start the API with OTLP export

```powershell
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
$env:Jwt__Key = "dev-only-key-change-in-production-at-least-32chars"
dotnet run --project day-1/QuotesApi
```

### Seed a few quotes and call the endpoint

```powershell
# Login
$r = Invoke-RestMethod -Method POST -Uri http://localhost:5000/api/auth/login `
     -ContentType "application/json" `
     -Body '{"email":"admin@example.com","password":"password123"}'
$token = $r.access_token

# Create 5 quotes
1..5 | ForEach-Object {
    Invoke-RestMethod -Method POST -Uri http://localhost:5000/api/quotes `
      -Headers @{Authorization="Bearer $token"} `
      -ContentType "application/json" `
      -Body "{`"author`":`"Author $_`",`"text`":`"Quote text $_`"}"
}

# Fetch the list (triggers N+1 or batched query depending on code state)
Invoke-RestMethod -Uri http://localhost:5000/api/quotes `
  -Headers @{Authorization="Bearer $token"}
```

Open **http://localhost:16686**, select service `QuotesApi`, and search for the
`GET /api/quotes` operation. The span tree shows either the staircase (N+1) or
the clean two-span tree (fixed).

---

## KQL queries for Azure Application Insights

### Detect slow GET /api/quotes requests (> 200 ms)

```kql
requests
| where timestamp > ago(24h)
| where name == "GET /api/quotes"
| where duration > 200
| order by duration desc
| project timestamp, duration, operation_Id, resultCode
```

### Detect N+1 pattern: many SELECT COUNT spans per trace

```kql
dependencies
| where timestamp > ago(24h)
| where type == "sqlite" or type == "SQL"
| where data has "COUNT"
| summarize count_queries = count() by operation_Id
| where count_queries > 5
| order by count_queries desc
| project operation_Id, count_queries
```

### Compare p50/p95 before vs after fix (requires timestamps of the two code states)

```kql
requests
| where timestamp > ago(7d)
| where name == "GET /api/quotes"
| summarize
    p50 = percentile(duration, 50),
    p95 = percentile(duration, 95),
    count = count()
  by bin(timestamp, 1h)
| render timechart
```

### Span strategy tag — confirm batched strategy is active

```kql
dependencies
| where timestamp > ago(1h)
| where name == "quotes.collection-counts"
| extend strategy = tostring(customDimensions["strategy"])
| summarize count() by strategy, bin(timestamp, 5m)
```

---

## Lessons

| Signal | What it showed |
|---|---|
| **Jaeger staircase** | N sequential child spans → synchronous loop calling DB per item |
| **Span tag `queries.issued`** | Exact number of extra round-trips without reading source code |
| **Serilog `LogWarning` + TraceId** | Cross-tool correlation: one search surfaces both the log warning and the full span tree |
| **EF Core instrumentation** | Zero code needed to make individual SQL queries visible as spans |
| **Custom span `quotes.collection-counts`** | Names the business operation; `strategy` tag confirms which code path ran |
