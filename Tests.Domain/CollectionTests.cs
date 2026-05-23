using FluentAssertions;
using QuotesApi.Models;
using Tests.Domain.Fakes;

namespace Tests.Domain;

public class CollectionTests
{
    private static Collection Make(string name = "My Collection") =>
        new(name, ownerId: 1);

    [Fact]
    public void Constructor_EmptyName_ThrowsArgumentException()
    {
        var act = () => new Collection("", ownerId: 1);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Name cannot be empty");
    }

    [Fact]
    public void Constructor_NameExceeds80Chars_ThrowsArgumentException()
    {
        var longName = new string('a', 81);

        var act = () => new Collection(longName, ownerId: 1);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Name must be between 3 and 80 characters");
    }

    [Fact]
    public void AddItem_51stItem_ThrowsInvalidOperationException()
    {
        var clock = new FakeClock();
        var collection = Make();
        for (var i = 1; i <= 50; i++) collection.AddItem(i, clock);

        var act = () => collection.AddItem(51, clock);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Maximum 50 items allowed");
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_ThrowsInvalidOperationException()
    {
        var clock = new FakeClock();
        var collection = Make();
        collection.AddItem(1, clock);

        var act = () => collection.AddItem(1, clock);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Duplicate quote is not allowed");
    }

    [Fact]
    public void RemoveItem_NonExistentItem_ThrowsInvalidOperationException()
    {
        var collection = Make();

        var act = () => collection.RemoveItem(99);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Quote not found in collection");
    }

    [Fact]
    public void AddItem_ThenRemoveItem_CollectionIsEmpty()
    {
        var collection = Make();
        collection.AddItem(1, new FakeClock());

        collection.RemoveItem(1);

        collection.Items.Should().BeEmpty();
    }
}
