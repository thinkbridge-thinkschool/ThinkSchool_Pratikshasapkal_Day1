using System.Net.Http.Json;

namespace QuotesApi.Services;

// ── QuoteEnrichmentService ────────────────────────────────────────────────────
//
// Consumes the "quote-enrichment" named HttpClient which is registered in
// Program.cs with a four-layer Polly resilience pipeline.  This class never
// configures the HttpClient itself — that is entirely the responsibility of the
// DI registration.  The service just calls the client and surfaces exceptions.
//
// WHY IHttpClientFactory instead of injecting HttpClient directly?
//
//   Injecting HttpClient as a singleton leads to DNS staleness (the handler is
//   never rotated).  Injecting as scoped causes socket exhaustion (a new socket
//   is opened per request and may not be released quickly enough by the GC).
//
//   IHttpClientFactory solves both: it returns a new HttpClient wrapper for
//   each call but reuses the pooled SocketsHttpHandler underneath, rotating
//   the handler on a configurable schedule (default: 2 minutes) so DNS changes
//   are respected without socket exhaustion.

public sealed class QuoteEnrichmentService : IQuoteEnrichmentService
{
    // Must match the name in Program.cs: AddHttpClient("quote-enrichment", …)
    private const string ClientName = "quote-enrichment";

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<QuoteEnrichmentService> _logger;

    public QuoteEnrichmentService(
        IHttpClientFactory factory,
        ILogger<QuoteEnrichmentService> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public async Task<QuoteEnrichmentResult> EnrichAsync(
        string text, CancellationToken ct = default)
    {
        // CreateClient() returns a new HttpClient wrapper but the underlying
        // SocketsHttpHandler (and the Polly DelegatingHandlers) are shared and
        // pooled — no socket exhaustion, no DNS staleness.
        var client = _factory.CreateClient(ClientName);

        _logger.LogDebug(
            "Calling enrichment service TextLength={TextLength}", text.Length);

        // ── Exceptions intentionally NOT caught here ──────────────────────────
        //
        // The Polly pipeline (registered in Program.cs) handles transient
        // failures transparently before any exception reaches this line:
        //
        //   network error / 5xx / 408 / 429
        //     → retried up to 3 times (exponential back-off, logged at Warning)
        //
        // If all retries fail, or a non-retryable exception occurs, it propagates:
        //   HttpRequestException           → retries exhausted
        //   BrokenCircuitException         → circuit OPEN, fail-fast
        //   TimeoutRejectedException       → budget exceeded
        //   OperationCanceledException     → caller cancelled
        //
        // This method's caller (the endpoint) maps these to the correct HTTP
        // status codes (502 / 503 / 504) — keeping transport concerns out of
        // this class.

        var response = await client.PostAsJsonAsync(
            "/v1/enrich",
            new { text },
            ct);

        // EnsureSuccessStatusCode throws HttpRequestException for 4xx / 5xx.
        // The retry layer already retried 5xx responses — if we still get one
        // here, all retries were exhausted; let it bubble up naturally.
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<QuoteEnrichmentResult>(ct)
               ?? throw new InvalidOperationException(
                   "Enrichment service returned an empty body.");
    }
}
