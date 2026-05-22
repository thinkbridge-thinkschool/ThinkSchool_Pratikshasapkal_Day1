namespace Quotes.Tests.Integration;

public sealed class QuoteEndpointTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public QuoteEndpointTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    // ── GET /api/quotes ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetQuotes_WithValidToken_Returns200AndJsonArray()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();

        // Act
        var response = await client.GetAsync("/api/quotes");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetQuotes_WithoutToken_Returns401()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/quotes");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/quotes/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task GetQuoteById_WhenExists_Returns200WithQuote()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();
        var body = QuoteBody("Marcus Aurelius", "You have power over your mind.");
        var createResp = await client.PostAsync("/api/quotes", body);
        var id = ParseId(await createResp.Content.ReadAsStringAsync());

        // Act
        var response = await client.GetAsync($"/api/quotes/{id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("author").GetString().Should().Be("Marcus Aurelius");
        doc.GetProperty("id").GetInt32().Should().Be(id);
    }

    [Fact]
    public async Task GetQuoteById_WhenNotFound_Returns404()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();

        // Act
        var response = await client.GetAsync("/api/quotes/99999");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/quotes ─────────────────────────────────────────────────────

    [Fact]
    public async Task PostQuote_WithValidToken_Returns201WithCreatedQuote()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();
        var body = QuoteBody("Epictetus", "Seek not that the things which happen should happen as you wish.");

        // Act
        var response = await client.PostAsync("/api/quotes", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("author").GetString().Should().Be("Epictetus");
        doc.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PostQuote_WithBlankAuthor_Returns400WithProblemDetail()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();
        var body = QuoteBody("   ", "Some perfectly valid text.");

        // Act
        var response = await client.PostAsync("/api/quotes", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Author must be between 1 and 200 characters.");
    }

    [Fact]
    public async Task PostQuote_WithoutToken_Returns401()
    {
        // Arrange
        var client = _factory.CreateClient();
        var body = QuoteBody("Seneca", "We suffer more in imagination than in reality.");

        // Act
        var response = await client.PostAsync("/api/quotes", body);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── DELETE /api/quotes/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task DeleteQuote_AsOwner_Returns200()
    {
        // Arrange
        var client = await _factory.CreateAuthenticatedClientAsync();
        var createResp = await client.PostAsync("/api/quotes",
            QuoteBody("Plato", "Courage is knowing what not to fear."));
        var id = ParseId(await createResp.Content.ReadAsStringAsync());

        // Act
        var response = await client.DeleteAsync($"/api/quotes/{id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteQuote_AsNonOwner_Returns403()
    {
        // Arrange — admin creates a quote
        var adminClient = await _factory.CreateAuthenticatedClientAsync();
        var createResp = await adminClient.PostAsync("/api/quotes",
            QuoteBody("Aristotle", "We are what we repeatedly do."));
        var id = ParseId(await createResp.Content.ReadAsStringAsync());

        // Arrange — register a second user directly via the test DB scope
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User("other@example.com", "other123"));
            await db.SaveChangesAsync();
        }

        // Arrange — get a token for the second user
        var otherClient = await _factory.CreateAuthenticatedClientAsync(
            "other@example.com", "other123");

        // Act
        var response = await otherClient.DeleteAsync($"/api/quotes/{id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteQuote_WithoutToken_Returns401()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.DeleteAsync("/api/quotes/1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StringContent QuoteBody(string author, string text) =>
        new($$"""{"author":"{{author}}","text":"{{text}}"}""",
            Encoding.UTF8, "application/json");

    private static int ParseId(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt32();

    public void Dispose() => _factory.Dispose();
}
