namespace Quotes.Tests.Integration;

/// <summary>
/// Integration tests for the /api/collections and /api/collections/{id}/items endpoints.
///
/// These run against a real SQL Server container and exercise the full EF Core owned-entity
/// path for Collection → CollectionItem (including the AddedAt column added in migration
/// 20260522000001_AddCollectionItemAddedAt).
///
/// POST /api/collections          ?name=&ownerId=    (query params — minimal API binding)
/// GET  /api/collections/{id}
/// POST /api/collections/{id}/items   ?quoteId=
/// DELETE /api/collections/{id}/items/{quoteId}
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionEndpointTests : IDisposable
{
    private readonly CustomWebApplicationFactory _factory;

    public CollectionEndpointTests(SqlServerContainerFixture fixture)
    {
        _factory = new CustomWebApplicationFactory(fixture);
    }

    // ── Authorization guards ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateCollection_WithoutToken_Returns401()
    {
        var adminId = GetAdminId();
        var response = await _factory.CreateClient()
            .PostAsync($"/api/collections?name=TestCollection&ownerId={adminId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCollection_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/collections/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AddItemToCollection_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient()
            .PostAsync("/api/collections/1/items?quoteId=42", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RemoveItemFromCollection_WithoutToken_Returns401()
    {
        var response = await _factory.CreateClient()
            .DeleteAsync("/api/collections/1/items/42");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/collections/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task GetCollection_WhenNotFound_Returns404()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.GetAsync("/api/collections/99999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCollection_WhenExists_Returns200WithCollection()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var id = await CreateCollectionAndGetId(client, "Philosophy Classics");

        var response = await client.GetAsync($"/api/collections/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("name").GetString().Should().Be("Philosophy Classics");
        doc.GetProperty("id").GetInt32().Should().Be(id);
    }

    // ── POST /api/collections ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateCollection_WithValidName_Returns201()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var adminId = GetAdminId();

        var response = await client.PostAsync(
            $"/api/collections?name=Stoicism&ownerId={adminId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("name").GetString().Should().Be("Stoicism");
        doc.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
        doc.GetProperty("items").GetArrayLength().Should().Be(0, because: "new collection has no items");
    }

    // ── POST /api/collections/{id}/items ─────────────────────────────────────

    [Fact]
    public async Task AddItemToCollection_WithValidQuoteId_Returns200WithItem()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var collId = await CreateCollectionAndGetId(client, "Favorites");

        // quoteId 42 does not need to exist in the Quotes table —
        // the collection stores references by id without a FK constraint.
        var response = await client.PostAsync(
            $"/api/collections/{collId}/items?quoteId=42", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var items = doc.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("quoteId").GetInt32().Should().Be(42);
        // Verify AddedAt was persisted (the column we added in the migration)
        items[0].GetProperty("addedAt").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task AddDuplicateItemToCollection_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var collId = await CreateCollectionAndGetId(client, "NoDuplicates");

        await client.PostAsync($"/api/collections/{collId}/items?quoteId=99", null);

        // Second add of the same quoteId
        var response = await client.PostAsync(
            $"/api/collections/{collId}/items?quoteId=99", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Duplicate quote is not allowed");
    }

    [Fact]
    public async Task AddItemToNonExistentCollection_Returns404()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/collections/99999/items?quoteId=1", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── DELETE /api/collections/{id}/items/{quoteId} ──────────────────────────

    [Fact]
    public async Task RemoveItemFromCollection_Returns200WithItemRemoved()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var collId = await CreateCollectionAndGetId(client, "ToTrim");
        await client.PostAsync($"/api/collections/{collId}/items?quoteId=7", null);

        var response = await client.DeleteAsync($"/api/collections/{collId}/items/7");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("items").GetArrayLength().Should().Be(0,
            because: "the only item was removed");
    }

    [Fact]
    public async Task RemoveNonExistentItemFromCollection_Returns400()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();
        var collId = await CreateCollectionAndGetId(client, "EmptyCollection");

        // quoteId 999 was never added
        var response = await client.DeleteAsync($"/api/collections/{collId}/items/999");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Quote not found in collection");
    }

    [Fact]
    public async Task RemoveItemFromNonExistentCollection_Returns404()
    {
        var client = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.DeleteAsync("/api/collections/99999/items/1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int GetAdminId()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider
            .GetRequiredService<AppDbContext>()
            .Users.Single(u => u.Email == "admin@example.com").Id;
    }

    private async Task<int> CreateCollectionAndGetId(HttpClient client, string name)
    {
        var adminId = GetAdminId();
        var resp = await client.PostAsync(
            $"/api/collections?name={Uri.EscapeDataString(name)}&ownerId={adminId}", null);
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetInt32();
    }

    public void Dispose() => _factory.Dispose();
}
