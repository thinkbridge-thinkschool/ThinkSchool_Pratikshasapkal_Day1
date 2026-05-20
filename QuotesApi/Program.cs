using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite("Data Source=quotes.db");
});

builder.Services.AddScoped<
    ICollectionRepository,
    CollectionRepository>();

var app = builder.Build();



// HOME ROUTE
app.MapGet("/", () =>
{
    return "Quotes API Running";
});



// CREATE QUOTE
app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var quote = new Quote
    {
        Author = request.Author,
        Text = request.Text
    };

    db.Quotes.Add(quote);

    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/quotes/{quote.Id}", quote);
});



// GET ALL QUOTES
app.MapGet("/api/quotes", async (
    AppDbContext db,
    CancellationToken cancellationToken,
    int page = 1,
    int size = 10) =>
{
    var quotes = await db.Quotes
        .OrderBy(q => q.Id)
        .Skip((page - 1) * size)
        .Take(size)
        .ToListAsync(cancellationToken);

    return Results.Ok(quotes);
});


app.MapGet("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var quotes = await db.Quotes
        .FindAsync(new object[] { id }, cancellationToken);

    if (quotes is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(quotes);
});



// DELETE QUOTE
app.MapDelete("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var quote = await db.Quotes
        .FindAsync(new object[] { id }, cancellationToken);

    if (quote is null)
    {
        return Results.NotFound();
    }

    db.Quotes.Remove(quote);

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        message = "Quote deleted successfully"
    });
});


app.MapGet("/api/collections/{id}", async (
    int id,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(id, cancellationToken);

    if (collection is null)
        return Results.NotFound();

    return Results.Ok(collection);
});

app.MapPost("/api/collections", async (
    string name,
    int ownerId,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = new Collection(
        name,
        ownerId);

    await repository.Add(
        collection,
        cancellationToken);

    return Results.Created(
        $"/api/collections/{collection.Id}",
        collection);
});

app.MapPost("/api/collections/{id}/items", async (
    int id,
    int quoteId,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(
        id,
        cancellationToken);

    if (collection == null)
    {
        return Results.NotFound();
    }

    try
    {
        collection.AddItem(quoteId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 400);
    }

    await repository.Update(
        collection,
        cancellationToken);

    return Results.Ok(collection);
});

app.MapDelete("/api/collections/{id}/items/{quoteId}", async (
    int id,
    int quoteId,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(
        id,
        cancellationToken);

    if (collection == null)
    {
        return Results.NotFound();
    }

    try
    {
        collection.RemoveItem(quoteId);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: 400);
    }

    await repository.Update(
        collection,
        cancellationToken);

    return Results.Ok(collection);
});

app.Run();