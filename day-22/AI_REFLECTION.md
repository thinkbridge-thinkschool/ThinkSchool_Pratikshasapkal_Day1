# Day 22 — Polly Resilience: Retry, Circuit Breaker, Timeout, Bulkhead

## Outbound Dependency

Named `HttpClient` **"unstable-api"** targets a **local upstream simulator**
endpoint (`/api/resilience/upstream-sim`) running on the same host (`http://localhost:5032/`).
The simulator accepts `?status=<code>&delay=<ms>` query params so tests are
fully deterministic and never depend on an external service.  The client has
`Timeout = InfiniteTimeSpan` so Polly owns every timeout; the built-in
`HttpClient` deadline is disabled.

## Resilience Pipeline (outer → inner)

```
Request → [Bulkhead] → [Retry] → [Circuit Breaker] → [Timeout] → HTTP call
```

Each layer sees failures from everything inside it.  The Retry wraps
Circuit Breaker + Timeout, so every failed attempt (including timeouts)
registers against the circuit-breaker failure ratio.

---

### 1. Bulkhead — Concurrency Limiter

```csharp
pipeline.AddConcurrencyLimiter(permitLimit: 3, queueLimit: 0);
```

- **PermitLimit = 3** — at most 3 outbound calls can be in-flight simultaneously.
- **QueueLimit = 0** — any call arriving when all 3 slots are busy is rejected
  *immediately* (throws `RateLimiterRejectedException`), never queued.
- **Why**: prevents a slow downstream from tying up all threads and starving
  the rest of the application.

---

### 2. Retry — Idempotent Methods Only

```csharp
pipeline.AddRetry(new HttpRetryStrategyOptions
{
    MaxRetryAttempts = 3,
    Delay            = 500 ms,
    BackoffType      = Exponential,
    UseJitter        = true,
    ShouldHandle     = /* checks idempotency + status code */
});
```

**Why retries are restricted to idempotent operations:**

An idempotent request (GET, HEAD, OPTIONS, PUT) produces the same outcome no
matter how many times it is sent.  Retrying a **POST** or **PATCH** risks
creating a duplicate resource, charging a payment twice, or sending an email
multiple times — side-effects the caller did not intend.

The `ShouldHandle` predicate checks `response.RequestMessage.Method` before
deciding to retry:

- `GET / HEAD / OPTIONS / PUT` → retry on 5xx or 429.
- `POST / PATCH / DELETE` → never retry.
- `BrokenCircuitException` → never retry (circuit already open).

---

### 3. Circuit Breaker

```csharp
pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
{
    FailureRatio      = 0.5,
    MinimumThroughput = 4,
    SamplingDuration  = 30 s,
    BreakDuration     = 8 s,
});
```

States:

| State | Behaviour |
|---|---|
| **Closed** | Normal operation — all requests pass through |
| **Open** | Fast-fail — throws `BrokenCircuitException` without touching the network |
| **Half-Open** | Sends one probe request after `BreakDuration` expires |

Opens when ≥ 50 % of the last 4+ requests within 30 s fail.
Stays open for 8 s to let the downstream recover.
After 8 s → Half-Open → one probe → if it succeeds, closes again.

---

### 4. Per-Attempt Timeout

```csharp
pipeline.AddTimeout(new TimeoutStrategyOptions { Timeout = 3 s });
```

Cancels any single attempt that takes longer than 3 seconds.
Throws `TimeoutRejectedException` which is caught and returned as HTTP 504.

---

## Structured Logging

Each layer logs its state transitions to `QuotesApi.Resilience`:

| Event | Log Level | Message pattern |
|---|---|---|
| Retry attempt | Warning | `[Retry] Attempt N/3 in X ms — 503` |
| Circuit opened | Error | `[CircuitBreaker] OPENED — fast-failing for 8 s` |
| Circuit half-open | Information | `[CircuitBreaker] HALF-OPEN — sending single probe` |
| Circuit closed | Information | `[CircuitBreaker] CLOSED — normal calls resumed` |
| Timeout | Warning | `[Timeout] Attempt cancelled after 3 s` |
| Bulkhead reject | Warning | logged in endpoint catch block |

---

## Proof: Circuit Opened and Recovered

### Step 1 — Confirm success baseline

```powershell
Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=200"
# → { upstream_status: 200, upstream_body: "200 OK" }
```

### Step 2 — Trigger retries (GET 500 — retry fires 3 times)

```powershell
Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=500"
```

API console shows three `[Retry] Attempt N/3` lines.

### Step 3 — Prove POST does NOT retry

```powershell
Invoke-RestMethod -Method POST "http://localhost:5032/api/resilience/probe?status=500"
```

API console shows **zero** `[Retry]` lines — one attempt, no retries.

### Step 4 — Trigger timeout

```powershell
Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=200&delay=5000"
# → HTTP 504  "Polly per-attempt timeout (3 s) exceeded."
```

API console shows `[Timeout] Attempt cancelled after 3 s` for each retry attempt.

### Step 5 — Force circuit breaker OPEN

```powershell
for ($i = 1; $i -le 6; $i++) {
    try { Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=500" }
    catch {}
    Write-Host "Request $i done"
}
```

After request 4 (MinimumThroughput reached, 100 % failure ratio), API console:

```
[CircuitBreaker] OPENED — fast-failing for 8 s. Trigger: 500
```

### Step 6 — Verify circuit is open (fast-fail)

```powershell
Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=200"
# → HTTP 503 immediately  "Circuit breaker is open"
```

Response comes back **instantly** — no network call, no retries.

### Step 7 — Wait 8 s and verify recovery

```powershell
Start-Sleep 10
Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=200"
# → { upstream_status: 200, ... }
```

API console shows:

```
[CircuitBreaker] HALF-OPEN — sending single probe request.
[CircuitBreaker] CLOSED — normal calls resumed.
```

### Step 8 — Bulkhead rejection

Fire 10 concurrent requests with a 2-second upstream delay (3 slots max):

```powershell
# PowerShell 7+
1..10 | ForEach-Object -Parallel {
    try { Invoke-RestMethod "http://localhost:5032/api/resilience/probe?status=200&delay=2000" }
    catch { Write-Host "Rejected: $_" }
} -ThrottleLimit 10
```

7 of the 10 requests return HTTP 429 "Bulkhead Rejected" immediately.
API console shows repeated `[Probe] Fast-fail — Bulkhead rejected`.

---

## Key Takeaways

1. **Retry without idempotency checks** is dangerous — duplicate writes,
   double charges, double notifications.
2. **Circuit breaker** breaks the feedback loop between a slow caller and a
   struggling downstream — once open, zero network calls escape to the broken
   service for 15 s.
3. **Timeout** prevents a single slow upstream call from holding a thread forever.
4. **Bulkhead** gives the rest of the application a survival budget — even if
   one downstream is completely dead, it can only consume 3 concurrent threads.
