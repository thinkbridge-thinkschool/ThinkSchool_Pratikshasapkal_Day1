using FluentAssertions;
using QuotesApi.Models;

namespace Tests.Domain;

public class CollectionTests
{
    // Fixed sentinel timestamp used wherever AddItem's addedAt value is irrelevant
    // to the behaviour under test (max-items, duplicate detection, remove).
    private static readonly DateTime AnyTime =
        new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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
        var collection = Make();
        for (var i = 1; i <= 50; i++) collection.AddItem(i, AnyTime);

        var act = () => collection.AddItem(51, AnyTime);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Maximum 50 items allowed");
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_ThrowsInvalidOperationException()
    {
        var collection = Make();
        collection.AddItem(1, AnyTime);

        var act = () => collection.AddItem(1, AnyTime);

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
        collection.AddItem(1, AnyTime);

        collection.RemoveItem(1);

        collection.Items.Should().BeEmpty();
    }
}
