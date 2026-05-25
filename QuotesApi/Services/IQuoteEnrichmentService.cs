namespace QuotesApi.Services;

// ── Quote Enrichment Service ──────────────────────────────────────────────────
//
// Calls an external NLP / enrichment API to derive metadata from quote text.
// The implementation (QuoteEnrichmentService) uses a named HttpClient
// "quote-enrichment" that is wrapped with a Polly v8 resilience pipeline:
//
//   • Retry          — 3× exponential back-off + jitter on transient failures
//   • Circuit Breaker — fast-fail when the downstream is clearly down
//   • Timeouts        — hard limit per attempt AND total budget
//
// EXCEPTION CONTRACT — nothing is swallowed:
//
//   HttpRequestException                    all retries exhausted, or non-transient HTTP error
//   Polly.CircuitBreaker.BrokenCircuitException   circuit is OPEN; request rejected immediately
//   Polly.Timeout.TimeoutRejectedException  total (10 s) or per-attempt (3 s) budget exceeded
//   OperationCanceledException              CancellationToken cancelled by the caller
//
// Callers translate these to HTTP 502 / 503 / 504 — see the demo endpoint in
// Program.cs ( GET /api/quotes/{id}/enriched ).

public interface IQuoteEnrichmentService
{
    /// <summary>
    /// Enrich <paramref name="text"/> with sentiment, confidence, and keywords.
    /// </summary>
    Task<QuoteEnrichmentResult> EnrichAsync(string text, CancellationToken ct = default);
}

/// <param name="Sentiment">  "Positive" | "Negative" | "Neutral"         </param>
/// <param name="Confidence"> 0.0 – 1.0, how certain the model is         </param>
/// <param name="Keywords">   Top keywords extracted from the text         </param>
public sealed record QuoteEnrichmentResult(
    string   Sentiment,
    double   Confidence,
    string[] Keywords);
