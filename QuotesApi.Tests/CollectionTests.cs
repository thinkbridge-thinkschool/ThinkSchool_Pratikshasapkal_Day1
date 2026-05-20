using QuotesApi.Models;
using QuotesApi.Tests.Fakes;

namespace QuotesApi.Tests;

public class CollectionTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public void AddItem_StampsAddedAt_WithClockTime()
    {
        var expectedTime = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        _clock.UtcNow = expectedTime;

        var collection = new Collection("My Quotes", ownerId: 1, _clock);
        collection.AddItem(quoteId: 42);

        Assert.Equal(expectedTime.UtcDateTime, collection.Items[0].AddedAt);
    }

    [Fact]
    public void AddItem_MultipleItems_EachUsesClockTimeAtMomentOfAdd()
    {
        var first = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        _clock.UtcNow = first;

        var collection = new Collection("My Quotes", ownerId: 1, _clock);
        collection.AddItem(1);

        _clock.Advance(TimeSpan.FromHours(2));
        collection.AddItem(2);

        Assert.Equal(first.UtcDateTime, collection.Items[0].AddedAt);
        Assert.Equal(first.AddHours(2).UtcDateTime, collection.Items[1].AddedAt);
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_Throws()
    {
        var collection = new Collection("My Quotes", ownerId: 1, _clock);
        collection.AddItem(1);

        var ex = Assert.Throws<InvalidOperationException>(() => collection.AddItem(1));
        Assert.Equal("Duplicate quote is not allowed", ex.Message);
    }

    [Fact]
    public void AddItem_ExceedsLimit_Throws()
    {
        var collection = new Collection("My Quotes", ownerId: 1, _clock);
        for (int i = 1; i <= 50; i++) collection.AddItem(i);

        var ex = Assert.Throws<InvalidOperationException>(() => collection.AddItem(51));
        Assert.Equal("Maximum 50 items allowed", ex.Message);
    }

    [Fact]
    public void RemoveItem_ExistingQuote_RemovesIt()
    {
        var collection = new Collection("My Quotes", ownerId: 1, _clock);
        collection.AddItem(42);

        collection.RemoveItem(42);

        Assert.Empty(collection.Items);
    }

    [Fact]
    public void RemoveItem_NonExistentQuote_Throws()
    {
        var collection = new Collection("My Quotes", ownerId: 1, _clock);

        var ex = Assert.Throws<InvalidOperationException>(() => collection.RemoveItem(99));
        Assert.Equal("Quote not found in collection", ex.Message);
    }

    [Fact]
    public void SetName_TooShort_Throws()
    {
        var collection = new Collection("Valid Name", ownerId: 1, _clock);

        var ex = Assert.Throws<ArgumentException>(() => collection.SetName("ab"));
        Assert.Equal("Name must be between 3 and 80 characters", ex.Message);
    }

    [Fact]
    public void SetName_Empty_Throws()
    {
        var collection = new Collection("Valid Name", ownerId: 1, _clock);

        var ex = Assert.Throws<ArgumentException>(() => collection.SetName(""));
        Assert.Equal("Name cannot be empty", ex.Message);
    }
}
