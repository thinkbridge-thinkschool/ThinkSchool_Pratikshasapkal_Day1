namespace QuotesApi.Models;

public class Quote
{
    public int Id { get; private set; }
    public string Author { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public bool IsDeleted { get; private set; }
    public string CreatedByEmail { get; private set; } = string.Empty;

    // Required by EF Core — never call directly.
    private Quote() { }

    private Quote(string author, string text, string createdByEmail)
    {
        Author = author;
        Text = text;
        CreatedByEmail = createdByEmail;
    }

    public static Result<Quote> Create(string author, string text, string createdByEmail)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Length > 200)
            return Result<Quote>.Fail("Author must be between 1 and 200 characters.");

        if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            return Result<Quote>.Fail("Text must be between 1 and 1000 characters.");

        return Result<Quote>.Ok(new Quote(author, text, createdByEmail));
    }

    public void Delete() => IsDeleted = true;
}