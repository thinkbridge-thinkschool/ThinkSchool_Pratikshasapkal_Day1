namespace QuotesApi.Models;

public class Collection
{
    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int OwnerId { get; private set; }

    public List<CollectionItem> Items { get; private set; } = new();

    /// <summary>
    /// Parameterless constructor required by EF Core for entity materialization.
    /// Not for application use — call <see cref="Collection(string, int)"/> instead.
    /// </summary>
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

    /// <summary>
    /// Adds a quote reference to this collection.
    /// </summary>
    /// <param name="quoteId">The ID of the quote to add.</param>
    /// <param name="addedAt">
    /// UTC timestamp to record as the item's creation time.
    /// Callers should pass <c>clock.UtcNow.UtcDateTime</c>.
    /// </param>
    public void AddItem(int quoteId, DateTime addedAt)
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

        Items.Add(new CollectionItem(quoteId, addedAt));
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

    /// <summary>
    /// private set — required for EF Core to populate this column when
    /// materializing the entity via the parameterless constructor.
    /// { get; } (readonly auto-property) has an initonly backing field that
    /// EF Core cannot write to after construction, leaving QuoteId = 0 and
    /// breaking duplicate detection and item-removal lookups.
    /// </summary>
    public int QuoteId { get; private set; }

    /// <summary>
    /// private set — same reason as QuoteId: EF Core needs a writable path
    /// to restore this value from the database during materialization.
    /// </summary>
    public DateTime AddedAt { get; private set; }

    public CollectionItem(
        int quoteId,
        DateTime addedAt)
    {
        QuoteId = quoteId;

        AddedAt = addedAt;
    }

    /// <summary>
    /// Parameterless constructor used by EF Core for entity materialization.
    /// EF Core calls this first, then sets QuoteId and AddedAt via private set.
    /// </summary>
    private CollectionItem()
    {
    }
}
