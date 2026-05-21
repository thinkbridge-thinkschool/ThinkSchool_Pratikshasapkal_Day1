using Microsoft.EntityFrameworkCore;
using QuotesApi.Abstractions;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Utilities;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Authorization;
using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;


var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"]!;
var jwtAudience = builder.Configuration["Jwt:Audience"]!;

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "DynamicScheme";
        options.DefaultChallengeScheme = "DynamicScheme";
    })

    .AddPolicyScheme(
        "DynamicScheme",
        "JWT or Entra",
        options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var authHeader =
                    context.Request.Headers.Authorization
                        .FirstOrDefault();

                if (authHeader?.StartsWith("Bearer ") == true)
                {
                    try
                    {
                        var jwt = new JwtSecurityTokenHandler()
                            .ReadJwtToken(authHeader["Bearer ".Length..]);

                        if (jwt.Issuer.Contains("login.microsoftonline.com"))
                            return "Entra";
                    }
                    catch { }
                }

                return JwtBearerDefaults.AuthenticationScheme;
            };
        })

    .AddJwtBearer(
        JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            options.TokenValidationParameters =
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.Zero,

                    ValidIssuer =
                        configuration["Jwt:Issuer"],

                    ValidAudience =
                        configuration["Jwt:Audience"],

                    IssuerSigningKey =
                        new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(
                                configuration["Jwt:Key"]!))
                };
        })

    .AddJwtBearer(
        "Entra",
        options =>
        {
            options.Authority =
                $"https://login.microsoftonline.com/{configuration["Entra:TenantId"]}/v2.0";

            options.TokenValidationParameters =
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,

                    ValidAudience =
                        configuration["Entra:Audience"]
                };
        });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("can-edit-quotes", policy =>
        policy.RequireClaim("scope", "quotes.write"));

    options.AddPolicy("can-delete-own-quotes", policy =>
        policy.AddRequirements(new DeleteOwnQuoteRequirement()));
});

builder.Services.AddScoped<IAuthorizationHandler, DeleteOwnQuoteHandler>();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite("Data Source=quotes.db");
});

builder.Services.AddScoped<
    ICollectionRepository,
    CollectionRepository>();

builder.Services.AddTransient<GuidGenerator>();
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddEndpointsApiExplorer();


string GenerateRefreshToken()
{
    var bytes = new byte[32];
    RandomNumberGenerator.Fill(bytes);
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

string HashToken(string token)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    return Convert.ToHexString(bytes).ToLower();
}

string CreateAccessToken(User user, IConfiguration cfg)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim("scope", "quotes.write")
    };

    var key = new SymmetricSecurityKey(
        Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));

    var jwtToken = new JwtSecurityToken(
        issuer: cfg["Jwt:Issuer"],
        audience: cfg["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(
            Convert.ToDouble(cfg["Jwt:ExpiryMinutes"])),
        signingCredentials: new SigningCredentials(
            key, SecurityAlgorithms.HmacSha256));

    return new JwtSecurityTokenHandler().WriteToken(jwtToken);
}

async Task RevokeFamily(AppDbContext db, string familyId, CancellationToken ct)
{
    var active = await db.RefreshTokens
        .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
        .ToListAsync(ct);

    foreach (var t in active)
        t.RevokedAt = DateTime.UtcNow;

    await db.SaveChangesAsync(ct);
}

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Home route
app.MapGet("/", () =>
{
    return "Quotes API Running";
});



// Create a new quote
app.MapPost("/api/quotes", async (
    CreateQuoteRequest request,
    AppDbContext db,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var userEmail = httpContext.User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
    var result = Quote.Create(request.Author, request.Text, userEmail);

    if (!result.IsSuccess)
        return Results.Problem(detail: result.Error, statusCode: 400);

    db.Quotes.Add(result.Value!);

    await db.SaveChangesAsync(cancellationToken);

    return Results.Created($"/api/quotes/{result.Value!.Id}", result.Value);
}).RequireAuthorization("can-edit-quotes");



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
}).RequireAuthorization();


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
}).RequireAuthorization();



// soft-delete a quote by id — ownership enforced by DeleteOwnQuoteHandler
app.MapDelete("/api/quotes/{id}", async (
    int id,
    AppDbContext db,
    IAuthorizationService authService,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var quote = await db.Quotes
        .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted, cancellationToken);

    if (quote is null)
        return Results.NotFound();

    var authResult = await authService.AuthorizeAsync(
        httpContext.User, quote, "can-delete-own-quotes");

    if (!authResult.Succeeded)
        return Results.Forbid();

    quote.Delete();

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new { message = "Quote deleted successfully" });
}).RequireAuthorization();


app.MapGet("/api/collections/{id}", async (
    int id,
    ICollectionRepository repository,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.GetById(id, cancellationToken);

    if (collection is null)
        return Results.NotFound();

    return Results.Ok(collection);
}).RequireAuthorization();

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
}).RequireAuthorization();

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
}).RequireAuthorization();

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
}).RequireAuthorization();

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    AppDbContext db,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var user = await db.Users
        .FirstOrDefaultAsync(x => x.Email == request.Email, cancellationToken);

    if (user is null || !user.VerifyPassword(request.Password))
        return Results.Unauthorized();

    var rawToken = GenerateRefreshToken();
    var expiryDays = Convert.ToInt32(configuration["Jwt:RefreshExpiryDays"] ?? "7");

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = HashToken(rawToken),
        UserId = user.Id,
        FamilyId = Guid.NewGuid().ToString(),
        ExpiresAt = DateTime.UtcNow.AddDays(expiryDays)
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        access_token = CreateAccessToken(user, configuration),
        refresh_token = rawToken,
        expires_in = 900
    });
});

app.MapPost("/api/auth/refresh", async (
    RefreshRequest request,
    AppDbContext db,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var tokenHash = HashToken(request.RefreshToken);

    var stored = await db.RefreshTokens
        .Include(t => t.User)
        .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    if (stored is null || stored.IsExpired)
        return Results.Unauthorized();

    // Reuse detected: legitimate holder already rotated this token.
    // Revoke the entire family to protect both parties.
    if (stored.IsUsed)
    {
        await RevokeFamily(db, stored.FamilyId, cancellationToken);
        return Results.Unauthorized();
    }

    if (stored.IsRevoked)
        return Results.Unauthorized();

    // Rotate: mark old token as consumed, issue a fresh one in the same family.
    var newRaw = GenerateRefreshToken();
    var newHash = HashToken(newRaw);
    var expiryDays = Convert.ToInt32(configuration["Jwt:RefreshExpiryDays"] ?? "7");

    stored.ReplacedByToken = newHash;

    db.RefreshTokens.Add(new RefreshToken
    {
        TokenHash = newHash,
        UserId = stored.UserId,
        FamilyId = stored.FamilyId,
        ExpiresAt = DateTime.UtcNow.AddDays(expiryDays)
    });

    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new
    {
        access_token = CreateAccessToken(stored.User, configuration),
        refresh_token = newRaw,
        expires_in = 900
    });
});




using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider
        .GetRequiredService<AppDbContext>();

    if (!db.Users.Any())
    {
        db.Users.Add(new User(
            "admin@example.com",
            "password123"));

        db.SaveChanges();
    }
}



app.Run();

public partial class Program { }