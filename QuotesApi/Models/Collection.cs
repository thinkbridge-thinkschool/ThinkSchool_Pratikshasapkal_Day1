namespace QuotesApi.Models;
using QuotesApi.Abstractions;

public class Collection
{
    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int OwnerId { get; private set; }

    public List<CollectionItem> Items { get; private set; } = new();

    private Collection() { }

    public Collection(
        string name,
        int ownerId)
    {
        SetName(name);

        OwnerId = ownerId;
    }

    public void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "Name cannot be empty");
        }

        if (name.Length < 3 || name.Length > 80)
        {
            throw new ArgumentException(
                "Name must be between 3 and 80 characters");
        }

        Name = name;
    }

    public void AddItem(int quoteId, IClock clock)
    {
        if (Items.Count >= 50)
        {
            throw new InvalidOperationException(
                "Maximum 50 items allowed");
        }

        if (Items.Any(x => x.QuoteId == quoteId))
        {
            throw new InvalidOperationException(
                "Duplicate quote is not allowed");
        }

        var nextId = Items.Count == 0 ? 1 : Items.Max(x => x.Id) + 1;

        Items.Add(new CollectionItem(
            quoteId,
            clock.UtcNow.UtcDateTime,
            nextId));
    }

    public void RemoveItem(int quoteId)
    {
        var item = Items.FirstOrDefault(
            x => x.QuoteId == quoteId);

        if (item == null)
        {
            throw new InvalidOperationException(
                "Quote not found in collection");
        }

        Items.Remove(item);
    }
}

public class CollectionItem
{
    public int Id { get; private set; }
    public int QuoteId { get; private set; }

    public DateTime AddedAt { get; private set; }

    public CollectionItem(
        int quoteId,
        DateTime addedAt,
        int id)
    {
        Id = id;
        QuoteId = quoteId;
        AddedAt = addedAt;
    }

    private CollectionItem()
    {
    }
}