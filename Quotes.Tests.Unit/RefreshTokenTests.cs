using FluentAssertions;
using QuotesApi.Models;

namespace Quotes.Tests.Unit;

public class RefreshTokenTests
{
    // ── IsExpired ────────────────────────────────────────────────────────────

    [Fact]
    public void IsExpired_ExpiryInFuture_ReturnsFalse()
    {
        // Arrange
        var token = new RefreshToken { ExpiresAt = DateTime.UtcNow.AddDays(7) };

        // Act
        var result = token.IsExpired;

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsExpired_ExpiryInPast_ReturnsTrue()
    {
        // Arrange
        var token = new RefreshToken { ExpiresAt = DateTime.UtcNow.AddDays(-1) };

        // Act
        var result = token.IsExpired;

        // Assert
        result.Should().BeTrue();
    }

    // ── IsRevoked ────────────────────────────────────────────────────────────

    [Fact]
    public void IsRevoked_RevokedAtIsNull_ReturnsFalse()
    {
        // Arrange
        var token = new RefreshToken
        {
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = null
        };

        // Act
        var result = token.IsRevoked;

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsRevoked_RevokedAtIsSet_ReturnsTrue()
    {
        // Arrange
        var token = new RefreshToken
        {
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            RevokedAt = DateTime.UtcNow
        };

        // Act
        var result = token.IsRevoked;

        // Assert
        result.Should().BeTrue();
    }

    // ── IsUsed ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsUsed_ReplacedByTokenIsNull_ReturnsFalse()
    {
        // Arrange
        var token = new RefreshToken
        {
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ReplacedByToken = null
        };

        // Act
        var result = token.IsUsed;

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsUsed_ReplacedByTokenIsSet_ReturnsTrue()
    {
        // Arrange — token was already rotated; replaying it triggers reuse detection
        var token = new RefreshToken
        {
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            ReplacedByToken = "e3b0c44298fc1c149afb"
        };

        // Act
        var result = token.IsUsed;

        // Assert
        result.Should().BeTrue();
    }

    // ── IsActive ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsActive_ValidToken_ReturnsTrue()
    {
        // Arrange — fresh token: not expired, not revoked, not yet rotated
        var token = new RefreshToken
        {
            ExpiresAt    = DateTime.UtcNow.AddDays(7),
            RevokedAt    = null,
            ReplacedByToken = null
        };

        // Act
        var result = token.IsActive;

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsActive_ExpiredToken_ReturnsFalse()
    {
        // Arrange
        var token = new RefreshToken
        {
            ExpiresAt    = DateTime.UtcNow.AddDays(-1),
            RevokedAt    = null,
            ReplacedByToken = null
        };

        // Act
        var result = token.IsActive;

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ReplayedToken_ReturnsFalse()
    {
        // Arrange — attacker replays a token that was already rotated;
        //           server must detect IsUsed and revoke the family
        var token = new RefreshToken
        {
            ExpiresAt    = DateTime.UtcNow.AddDays(7),
            RevokedAt    = null,
            ReplacedByToken = "a9f3c1d2e4b5" // rotation already happened
        };

        // Act
        var result = token.IsActive;

        // Assert
        result.Should().BeFalse();
        token.IsUsed.Should().BeTrue();
    }

    [Fact]
    public void IsActive_FamilyMemberRevokedAfterReuseDetection_ReturnsFalse()
    {
        // Arrange — legitimate successor token whose family was purged because
        //           a sibling was replayed; RevokeFamily() sets RevokedAt
        var token = new RefreshToken
        {
            ExpiresAt    = DateTime.UtcNow.AddDays(7),
            RevokedAt    = DateTime.UtcNow,
            ReplacedByToken = null
        };

        // Act
        var result = token.IsActive;

        // Assert
        result.Should().BeFalse();
        token.IsRevoked.Should().BeTrue();
    }

    [Fact]
    public void IsActive_AllInvalidConditions_ReturnsFalse()
    {
        // Arrange — token is expired, revoked, and consumed simultaneously
        var token = new RefreshToken
        {
            ExpiresAt    = DateTime.UtcNow.AddDays(-1),
            RevokedAt    = DateTime.UtcNow,
            ReplacedByToken = "deadbeef"
        };

        // Act
        var result = token.IsActive;

        // Assert
        result.Should().BeFalse();
        token.IsExpired.Should().BeTrue();
        token.IsRevoked.Should().BeTrue();
        token.IsUsed.Should().BeTrue();
    }
}
