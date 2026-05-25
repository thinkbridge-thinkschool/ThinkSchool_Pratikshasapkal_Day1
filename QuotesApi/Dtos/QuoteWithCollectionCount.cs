namespace QuotesApi.Dtos;

/// <summary>
/// Projection returned by GET /api/quotes.
/// CollectionCount = how many CollectionItems across all collections reference this quote.
/// </summary>
public sealed record QuoteWithCollectionCount(
    int Id,
    string Author,
    string Text,
    string CreatedByEmail,
    bool IsDeleted,
    int CollectionCount);
