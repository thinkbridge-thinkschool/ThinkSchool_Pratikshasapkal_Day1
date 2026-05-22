using FluentAssertions;
using NSubstitute;
using QuotesApi.Abstractions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class CollectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetName_NullOrWhitespaceName_ThrowsArgumentException(string? name)
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var collection = new Collection("ValidName", 1, clock);

        // Act
        var act = () => collection.SetName(name!);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("Name cannot be empty");
    }

    [Fact]
    public void SetName_NameTooShort_ThrowsArgumentException()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var collection = new Collection("ValidName", 1, clock);

        // Act
        var act = () => collection.SetName("AB");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("Name must be between 3 and 80 characters");
    }

    [Fact]
    public void SetName_NameTooLong_ThrowsArgumentException()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var collection = new Collection("ValidName", 1, clock);
        var tooLong = new string('X', 81);

        // Act
        var act = () => collection.SetName(tooLong);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("Name must be between 3 and 80 characters");
    }

    [Fact]
    public void SetName_ValidName_SetsName()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var collection = new Collection("ValidName", 1, clock);

        // Act
        collection.SetName("My Favourites");

        // Assert
        collection.Name.Should().Be("My Favourites");
    }

    [Fact]
    public void AddItem_WhenAtCapacity_ThrowsInvalidOperationException()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var collection = new Collection("ValidName", 1, clock);
        for (var i = 1; i <= 50; i++)
            collection.AddItem(i);

        // Act
        var act = () => collection.AddItem(51);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Maximum 50 items allowed");
    }

    [Fact]
    public void AddItem_DuplicateQuoteId_ThrowsInvalidOperationException()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var collection = new Collection("ValidName", 1, clock);
        collection.AddItem(42);

        // Act
        var act = () => collection.AddItem(42);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Duplicate quote is not allowed");
    }

    [Fact]
    public void AddItem_ValidQuoteId_AddsItemToCollection()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var collection = new Collection("ValidName", 1, clock);

        // Act
        collection.AddItem(7);

        // Assert
        collection.Items.Should().ContainSingle(i => i.QuoteId == 7);
    }

    [Fact]
    public void RemoveItem_QuoteNotInCollection_ThrowsInvalidOperationException()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        var collection = new Collection("ValidName", 1, clock);

        // Act
        var act = () => collection.RemoveItem(99);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Quote not found in collection");
    }

    [Fact]
    public void RemoveItem_ExistingQuote_RemovesItemFromCollection()
    {
        // Arrange
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var collection = new Collection("ValidName", 1, clock);
        collection.AddItem(5);

        // Act
        collection.RemoveItem(5);

        // Assert
        collection.Items.Should().BeEmpty();
    }

    [Fact]
    public void AddItem_ValidQuoteId_StampsAddedAtFromClock()
    {
        // Arrange
        var fixedTime = new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(fixedTime);
        var collection = new Collection("ValidName", 1, clock);

        // Act
        collection.AddItem(7);

        // Assert
        collection.Items[0].AddedAt.Should().Be(fixedTime.UtcDateTime);
    }

    [Fact]
    public void AddItem_MultipleItems_EachItemRecordsItsOwnClockTimestamp()
    {
        // Arrange
        var firstTime  = new DateTimeOffset(2024, 1,  1,  0,  0, 0, TimeSpan.Zero);
        var secondTime = new DateTimeOffset(2024, 6, 15, 12,  0, 0, TimeSpan.Zero);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(firstTime, secondTime);
        var collection = new Collection("ValidName", 1, clock);

        // Act
        collection.AddItem(1);
        collection.AddItem(2);

        // Assert
        collection.Items[0].AddedAt.Should().Be(firstTime.UtcDateTime);
        collection.Items[1].AddedAt.Should().Be(secondTime.UtcDateTime);
    }
}
