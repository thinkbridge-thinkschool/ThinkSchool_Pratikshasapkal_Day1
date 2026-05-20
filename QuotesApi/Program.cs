using Microsoft.EntityFrameworkCore;
using QuotesApi.Abstractions;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Utilities;


var builder = WebApplication.CreateBuilder(args);



builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite("Data Source=quotes.db");
});

builder.Services.AddScoped<
    ICollectionRepository,
    CollectionRepository>();

builder.Services.AddTransient<GuidGenerator>();
builder.Services.AddSingleton<IClock, SystemClock>();

var app = builder.Build();

// Home route
app.MapGet("/", () =>
{
    return "Quotes API Running";
});



// Create a new quote
app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var result = Quote.Create(request.Author, request.Text);

    if (!result.IsSuccess)
        return Results.Problem(detail: result.Error, statusCode: 400);

    db.Quotes.Add(result.Value!);

    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/quotes/{result.Value!.Id}", result.Value);
});



// get all quotes with pagination
app.MapGet("/api/quotes", async (
    AppDbContext db,
    CancellationToken cancellationToken,
    int page = 1,
    int size = 10) =>
{
    var quotes = await db.Quotes
        .Where(q => !q.IsDeleted)
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
    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    return Results.Ok(quote);
});



// soft-delete a quote by id
app.MapDelete("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    quote.Delete();

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new { message = "Quote deleted successfully" });
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
    IClock clock,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = new Collection(
        name,
        ownerId,
        clock);

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