using FluentAssertions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class QuoteTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankOrNullAuthor_ReturnsFailure(string? author)
    {
        // Arrange
        const string text = "Valid quote text.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author!, text, email);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Author must be between 1 and 200 characters.");
    }

    [Fact]
    public void Create_AuthorExceedsMaxLength_ReturnsFailure()
    {
        // Arrange
        var author = new string('A', 201);
        const string text = "Valid quote text.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Author must be between 1 and 200 characters.");
    }

    [Fact]
    public void Create_AuthorAtExactMaxLength_ReturnsSuccess()
    {
        // Arrange
        var author = new string('A', 200);
        const string text = "Valid quote text.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankOrNullText_ReturnsFailure(string? text)
    {
        // Arrange
        const string author = "Valid Author";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text!, email);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Text must be between 1 and 1000 characters.");
    }

    [Fact]
    public void Create_TextExceedsMaxLength_ReturnsFailure()
    {
        // Arrange
        const string author = "Valid Author";
        var text = new string('x', 1001);
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Text must be between 1 and 1000 characters.");
    }

    [Fact]
    public void Create_TextAtExactMaxLength_ReturnsSuccess()
    {
        // Arrange
        const string author = "Valid Author";
        var text = new string('x', 1000);
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_AuthorWithMinimumLength_ReturnsSuccess()
    {
        // Arrange
        const string author = "A";
        const string text = "Valid quote text.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_TextWithMinimumLength_ReturnsSuccess()
    {
        // Arrange
        const string author = "Valid Author";
        const string text = "X";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_Failure_ValueIsNull()
    {
        // Arrange
        const string author = "";
        const string text = "Valid text.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void Create_Success_ErrorIsNull()
    {
        // Arrange
        const string author = "Valid Author";
        const string text = "Valid text.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Create_ValidInputs_ReturnsSuccessWithPopulatedQuote()
    {
        // Arrange
        const string author = "Marcus Aurelius";
        const string text = "You have power over your mind, not outside events.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
        result.Value!.Author.Should().Be(author);
        result.Value.Text.Should().Be(text);
        result.Value.CreatedByEmail.Should().Be(email);
    }

    [Fact]
    public void Create_ValidQuote_IsNotDeletedByDefault()
    {
        // Arrange
        const string author = "Marcus Aurelius";
        const string text = "Accept what you cannot change.";
        const string email = "user@example.com";

        // Act
        var result = Quote.Create(author, text, email);

        // Assert
        result.Value!.IsDeleted.Should().BeFalse();
    }
}
