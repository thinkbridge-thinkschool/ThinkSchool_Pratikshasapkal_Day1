namespace QuotesApi.Models;

public class Author
{
    public int Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public ICollection<Quote> Quotes { get; } = new List<Quote>();

    private Author() { }

    public Author(string name)
    {
        Name = name;
    }
}
