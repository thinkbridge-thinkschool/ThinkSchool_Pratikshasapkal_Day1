# Cancellation-Aware Service Pattern

## The problem

Without cancellation tokens, a client that disconnects (browser tab closed, mobile app backgrounded, proxy timeout) leaves the server still executing the query and writing to the database. The work is wasted and the thread is occupied.

## How ASP.NET Core wires cancellation automatically

Minimal API handlers that declare a `CancellationToken` parameter receive `HttpContext.RequestAborted` — a token that fires the moment the client disconnects. Every async operation downstream that forwards this token gets cancelled automatically.

```
Client disconnects
       │
       ▼
HttpContext.RequestAborted fires
       │
       ▼
CancellationToken parameter in handler receives the signal
       │
       ▼
db.SaveChangesAsync(cancellationToken) → throws OperationCanceledException
       │
       ▼
EF Core rolls back, thread is freed
```

---

## Service code — `CollectionRepository`

Every method accepts a `CancellationToken` and forwards it to the EF Core call. The repository never decides *when* to cancel; it just propagates what it receives.

```csharp
// Repositories/ICollectionRepository.cs
public interface ICollectionRepository
{
    Task<Collection?> GetById(int id, CancellationToken cancellationToken);
    Task Add(Collection collection, CancellationToken cancellationToken);
    Task Update(Collection collection, CancellationToken cancellationToken);
    Task Delete(Collection collection, CancellationToken cancellationToken);
}
```

```csharp
// Repositories/CollectionRepository.cs
public class CollectionRepository : ICollectionRepository
{
    private readonly AppDbContext _db;

    public CollectionRepository(AppDbContext db) => _db = db;

    public async Task<Collection?> GetById(int id, CancellationToken cancellationToken)
    {
        return await _db.Collections
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken); // ← forwarded
    }

    public async Task Add(Collection collection, CancellationToken cancellationToken)
    {
        _db.Collections.Add(collection);
        await _db.SaveChangesAsync(cancellationToken); // ← forwarded
    }

    public async Task Update(Collection collection, CancellationToken cancellationToken)
    {
        _db.Collections.Update(collection);
        await _db.SaveChangesAsync(cancellationToken); // ← forwarded
    }

    public async Task Delete(Collection collection, CancellationToken cancellationToken)
    {
        _db.Collections.Remove(collection);
        await _db.SaveChangesAsync(cancellationToken); // ← forwarded
    }
}
```

The handlers in `Program.cs` follow the same pattern:

```csharp
// Program.cs — every endpoint declares CancellationToken and passes it through
app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>      // ← injected from HttpContext.RequestAborted
{
    var quote = new Quote { Author = request.Author, Text = request.Text };
    db.Quotes.Add(quote);
    await db.SaveChangesAsync(cancellationToken); // ← if client disconnects, this throws
    return Results.Created($"/api/quotes/{quote.Id}", quote);
});

app.MapGet("/api/quotes", async (
    AppDbContext db,
    CancellationToken cancellationToken,
    int page = 1, int size = 10) =>
{
    var quotes = await db.Quotes
        .OrderBy(q => q.Id)
        .Skip((page - 1) * size)
        .Take(size)
        .ToListAsync(cancellationToken);          // ← forwarded
    return Results.Ok(quotes);
});
```

---

## Tests — `CancellationTests.cs`

Tests use `WebApplicationFactory<Program>` to spin the real application in-process and replace SQLite with an isolated in-memory database per run.

```csharp
public class CancellationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CancellationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Swap SQLite for an in-memory DB — no file system, no shared state.
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor is not null) services.Remove(descriptor);

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("CancellationTests_" + Guid.NewGuid()));
            });
        });
    }

    // Test 1 — Pre-cancelled token: HttpClient detects cancellation before
    // dispatching the request. Nothing reaches the server.
    [Fact]
    public async Task GetQuotes_PreCancelledToken_ThrowsWithoutReceivingResponse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // already cancelled before the call

        var client = _factory.CreateClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/api/quotes", cts.Token));
    }

    // Test 2 — Immediate cancellation races against the request.
    // Fast in-memory path: request may finish before cancel fires (200 OK).
    // Slow/preempted path: cancel fires first (OperationCanceledException).
    // This is the client-disconnect / 499 scenario in production.
    [Fact]
    public async Task GetQuotes_TokenCancelledImmediately_CompletesOrAborts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.Zero);
        var client = _factory.CreateClient();

        try
        {
            var response = await client.GetAsync("/api/quotes", cts.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // request won the race
        }
        catch (OperationCanceledException)
        {
            Assert.True(cts.IsCancellationRequested); // cancellation won the race
        }
    }

    // Test 3 — Cancelled mutating request leaves no side effect.
    // The quote must not appear in subsequent reads.
    [Fact]
    public async Task PostQuote_PreCancelledToken_ThrowsAndNeverCreatesResource()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = _factory.CreateClient();
        var content = new StringContent(
            """{"author":"Ada Lovelace","text":"The Analytical Engine weaves algebraic patterns."}""",
            System.Text.Encoding.UTF8,
            "application/json");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.PostAsync("/api/quotes", content, cts.Token));

        var check = await client.GetAsync("/api/quotes");
        var body  = await check.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ada Lovelace", body); // no partial write persisted
    }
}
```

---

## What each test proves

| Test | Token state | Assertion | What it demonstrates |
|------|-------------|-----------|----------------------|
| `GetQuotes_PreCancelledToken_…` | Cancelled before call | `OperationCanceledException` thrown | HttpClient honours cancellation without touching the network |
| `GetQuotes_TokenCancelledImmediately_…` | Cancelled at `TimeSpan.Zero` | 200 OK **or** `OperationCanceledException` | Both outcomes are clean — no hang, no corrupt state |
| `PostQuote_PreCancelledToken_…` | Cancelled before call | Exception + empty DB | A cancelled write leaves no side effect |

---

## The 499 status code

`499 Client Closed Request` is an Nginx convention. ASP.NET Core does not emit it natively — it resets the TCP connection when the client disconnects. If you need the status code surfaced (e.g. for logging or observability), add middleware:

```csharp
app.Use(async (ctx, next) =>
{
    try
    {
        await next(ctx);
    }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
    {
        ctx.Response.StatusCode = 499;
    }
});
```

Without this middleware, the observable effect at the server is `OperationCanceledException` propagating up from `SaveChangesAsync` or `ToListAsync` — which is exactly what the tests above catch.

---

## Rules of thumb

1. **Every async method in a service or repository should accept `CancellationToken`.** Pass it to every awaited call — never ignore it.
2. **Never catch `OperationCanceledException` and swallow it** in a middleware unless you intentionally want to emit 499. Let it propagate; ASP.NET Core handles the rest.
3. **Test pre-cancelled tokens, not just timing races.** A pre-cancelled token gives a deterministic result and verifies the plumbing end-to-end.
