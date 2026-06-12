namespace QuotesApi.Dtos;

// Serialization-safe projection used as the HybridCache value type.
// Quote's EF Core entity uses private setters and a private constructor which
// System.Text.Json cannot reconstruct during L2 deserialization.
public sealed record QuoteResponse(
    int Id,
    string Author,
    string Text,
    bool IsDeleted,
    string CreatedByEmail);
