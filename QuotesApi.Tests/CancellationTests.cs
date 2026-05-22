using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuotesApi.Data;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace QuotesApi.Tests;

public class CancellationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CancellationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                builder.UseEnvironment("Testing");
                // Replace SQLite with an isolated in-memory database so tests
                // don't touch the file system and don't share state.
                var descriptors = services
                    .Where(d =>
                        d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                        d.ServiceType == typeof(AppDbContext) ||
                        (d.ServiceType.IsGenericType &&
                         d.ServiceType.GetGenericArguments().Contains(typeof(AppDbContext))))
                    .ToList();

                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("CancellationTests_" + Guid.NewGuid()));
            });
        });
    }

    // A pre-cancelled token is detected by HttpClient before the request is dispatched.
    // The operation never reaches the server — no response is received.
    [Fact]
    public async Task GetQuotes_PreCancelledToken_ThrowsWithoutReceivingResponse()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var client = _factory.CreateClient();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/api/quotes", cts.Token));
    }

    // A token cancelled with zero delay races against the in-process request.
    // Both outcomes are correct: if the request finishes first it must be 200 OK;
    // if cancellation wins the call throws OperationCanceledException.
    // Either way there is no partial or undefined state.
    [Fact]
    public async Task GetQuotes_TokenCancelledImmediately_CompletesOrAborts()
    {
        using var cts = new CancellationTokenSource(TimeSpan.Zero);
        var client = _factory.CreateClient();

        try
        {
            var response = await client.GetAsync("/api/quotes", cts.Token);
            // Request won the race — must be a clean success (200 OK).
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Cancellation won — the operation did not complete.
            // In a real server returning 499 for client disconnects, this is that path.
            Assert.True(cts.IsCancellationRequested);
        }
    }

    // A pre-cancelled token on a mutating endpoint means the write is never attempted.
    // The resource must not exist afterward — no 201 Created, no persisted row.
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

        // Verify the quote was never persisted by querying without a token.
        var check = await client.GetAsync("/api/quotes");
        var body = await check.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Ada Lovelace", body);
    }
}
