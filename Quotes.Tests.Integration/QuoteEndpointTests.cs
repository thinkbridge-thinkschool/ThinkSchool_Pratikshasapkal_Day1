namespace Quotes.Tests.Integration;

[Collection("SqlServer")]
public sealed class QuoteEndpointTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public QuoteEndpointTests(SqlServerContainerFixture fixture)
    {
        _factory = new CustomWebApplicationFactory(fixture);
    }

    // ── GET /api/quotes ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetQuotes_WithValidToken_Returns200AndEmptyArray()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.GetArrayLength().Should().Be(0, because: "fresh DB has no quotes");
    }

    [Fact]
    public async Task GetQuotes_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetQuotes_PaginatesCorrectly()
    {
        // Arrange — create 12 quotes so we can split across pages
        var client = await _factory.CreateAuthenticatedClientAsync();
        for (var i = 1; i <= 12; i++)
            await client.PostAsync("/api/quotes", QuoteBody($"Author {i}", $"Text {i}"));

        // Act — page 2 of size 5 should contain items 6-10
        var page2 = await client.GetAsync("/api/quotes?page=2&size=5");
        var page3 = await client.GetAsync("/api/quotes?page=3&size=5");

        // Assert
        page2.StatusCode.Should().Be(HttpStatusCode.OK);
        var p2Root = JsonDocument.Parse(await page2.Content.ReadAsStringAsync()).RootElement;
        p2Root.GetArrayLength().Should().Be(5, because: "page 2 of 5 covers items 6-10 of 12");

        page3.StatusCode.Should().Be(HttpStatusCode.OK);
        var p3Root = JsonDocument.Parse(await page3.Content.ReadAsStringAsync()).RootElement;
        p3Root.GetArrayLength().Should().Be(2, because: "page 3 of 5 covers only items 11-12 of 12");
    }

    // ── GET /api/quotes/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task GetQuoteById_WhenExists_Returns200WithQuote()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateQuoteAndGetId(client, "Marcus Aurelius", "You have power over your mind.");

        var response = await client.GetAsync($"/api/quotes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("author").GetString().Should().Be("Marcus Aurelius");
        doc.GetProperty("id").GetInt32().Should().Be(id);
    }

    [Fact]
    public async Task GetQuoteById_WhenNotFound_Returns404()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/quotes/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetQuoteById_WhenSoftDeleted_Returns404()
    {
        // Arrange — create then soft-delete a quote
        var client = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateQuoteAndGetId(client, "Seneca", "He who fears death will never do anything worthy of a living man.");
        await client.DeleteAsync($"/api/quotes/{id}");

        // Act — deleted quotes must be invisible to the GET endpoint
        var response = await client.GetAsync($"/api/quotes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── POST /api/quotes ─────────────────────────────────────────────────────

    [Fact]
    public async Task PostQuote_WithValidPayload_Returns201WithLocation()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/quotes",
            QuoteBody("Epictetus", "Seek not that the things which happen should happen as you wish."));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("author").GetString().Should().Be("Epictetus");
        doc.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PostQuote_StoresCreatedByEmail()
    {
        var client = await _factory.CreateAuthenticatedClientAsync("admin@example.com", "password123");

        var response = await client.PostAsync("/api/quotes",
            QuoteBody("Plato", "The heaviest penalty for declining to rule is to be ruled by someone inferior to yourself."));
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var id = doc.GetProperty("id").GetInt32();

        // Verify createdByEmail is persisted and returned by the GET endpoint
        var getResp = await client.GetAsync($"/api/quotes/{id}");
        var getDoc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync()).RootElement;
        getDoc.GetProperty("createdByEmail").GetString().Should().Be("admin@example.com");
    }

    [Fact]
    public async Task PostQuote_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient().PostAsync("/api/quotes",
            QuoteBody("Seneca", "We suffer more in imagination than in reality."));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostQuote_WithBlankAuthor_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/quotes", QuoteBody("   ", "Some perfectly valid text."));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Author must be between 1 and 200 characters.");
    }

    [Fact]
    public async Task PostQuote_WithBlankText_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/quotes", QuoteBody("Aristotle", "   "));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Text must be between 1 and 1000 characters.");
    }

    [Fact]
    public async Task PostQuote_WithAuthorOver200Chars_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var tooLong = new string('A', 201);

        var response = await client.PostAsync("/api/quotes", QuoteBody(tooLong, "Valid text."));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Author must be between 1 and 200 characters.");
    }

    [Fact]
    public async Task PostQuote_WithTextOver1000Chars_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var tooLong = new string('T', 1001);

        var response = await client.PostAsync("/api/quotes", QuoteBody("Aristotle", tooLong));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Text must be between 1 and 1000 characters.");
    }

    // ── DELETE /api/quotes/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task DeleteQuote_AsOwner_Returns200()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateQuoteAndGetId(client, "Plato", "Courage is knowing what not to fear.");

        var response = await client.DeleteAsync($"/api/quotes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeleteQuote_AsOwner_SoftDeletesQuote_NotVisibleAfterwards()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateQuoteAndGetId(client, "Aristotle", "Excellence is never an accident.");

        await client.DeleteAsync($"/api/quotes/{id}");

        // Soft-deleted quote must vanish from the list
        var listResp = await client.GetAsync("/api/quotes");
        var items = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync()).RootElement;
        items.GetArrayLength().Should().Be(0, because: "the only quote was soft-deleted");
    }

    [Fact]
    public async Task DeleteQuote_AsNonOwner_Returns403()
    {
        // Admin creates a quote
        var adminClient = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateQuoteAndGetId(adminClient, "Aristotle", "We are what we repeatedly do.");

        // Register a second user and get their token
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(new User("other@example.com", "other123"));
            await db.SaveChangesAsync();
        }
        var otherClient = await _factory.CreateAuthenticatedClientAsync("other@example.com", "other123");

        var response = await otherClient.DeleteAsync($"/api/quotes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteQuote_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient().DeleteAsync("/api/quotes/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteQuote_WhenNotFound_Returns404()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync("/api/quotes/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteQuote_WhenAlreadyDeleted_Returns404()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateQuoteAndGetId(client, "Epictetus", "No man is free who is not master of himself.");

        await client.DeleteAsync($"/api/quotes/{id}"); // first delete — succeeds

        // Second delete: quote is already soft-deleted, endpoint returns 404
        var response = await client.DeleteAsync($"/api/quotes/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StringContent QuoteBody(string author, string text) =>
        new($$"""{"author":"{{author}}","text":"{{text}}"}""",
            Encoding.UTF8, "application/json");

    private static async Task<int> CreateQuoteAndGetId(HttpClient client, string author, string text)
    {
        var resp = await client.PostAsync("/api/quotes",
            new StringContent($$"""{"author":"{{author}}","text":"{{text}}"}""",
                Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetInt32();
    }

    public void Dispose() => _factory.Dispose();
}
