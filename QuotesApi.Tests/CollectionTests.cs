using QuotesApi.Models;

namespace QuotesApi.Tests;

public class CollectionTests
{
    [Fact]
    public void AddItem_StampsAddedAt_WithSuppliedTime()
    {
        var expectedTime = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        var collection = new Collection("My Quotes", ownerId: 1);
        collection.AddItem(quoteId: 42, addedAt: expectedTime);

        Assert.Equal(expectedTime, collection.Items[0].AddedAt);
    }

    [Fact]
    public void AddItem_MultipleItems_EachRecordsItsOwnAddedAt()
    {
        var first  = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var second = first.AddHours(2);

        var collection = new Collection("My Quotes", ownerId: 1);
        collection.AddItem(1, first);
        collection.AddItem(2, second);

        Assert.Equal(first,  collection.Items[0].AddedAt);
        Assert.Equal(second, collection.Items[1].AddedAt);
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_Throws()
    {
        var collection = new Collection("My Quotes", ownerId: 1);
        collection.AddItem(1, DateTime.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(
            () => collection.AddItem(1, DateTime.UtcNow));
        Assert.Equal("Duplicate quote is not allowed", ex.Message);
    }

    [Fact]
    public void AddItem_ExceedsLimit_Throws()
    {
        var collection = new Collection("My Quotes", ownerId: 1);
        for (int i = 1; i <= 50; i++) collection.AddItem(i, DateTime.UtcNow);

        var ex = Assert.Throws<InvalidOperationException>(
            () => collection.AddItem(51, DateTime.UtcNow));
        Assert.Equal("Maximum 50 items allowed", ex.Message);
    }

    [Fact]
    public void RemoveItem_ExistingQuote_RemovesIt()
    {
        var collection = new Collection("My Quotes", ownerId: 1);
        collection.AddItem(42, DateTime.UtcNow);

        collection.RemoveItem(42);

        Assert.Empty(collection.Items);
    }

    [Fact]
    public void RemoveItem_NonExistentQuote_Throws()
    {
        var collection = new Collection("My Quotes", ownerId: 1);

        var ex = Assert.Throws<InvalidOperationException>(
            () => collection.RemoveItem(99));
        Assert.Equal("Quote not found in collection", ex.Message);
    }

    [Fact]
    public void SetName_TooShort_Throws()
    {
        var collection = new Collection("Valid Name", ownerId: 1);

        var ex = Assert.Throws<ArgumentException>(() => collection.SetName("ab"));
        Assert.Equal("Name must be between 3 and 80 characters", ex.Message);
    }

    [Fact]
    public void SetName_Empty_Throws()
    {
        var collection = new Collection("Valid Name", ownerId: 1);

        var ex = Assert.Throws<ArgumentException>(() => collection.SetName(""));
        Assert.Equal("Name cannot be empty", ex.Message);
    }
}
