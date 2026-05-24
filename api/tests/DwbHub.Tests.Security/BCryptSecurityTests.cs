using DwbHub.Infrastructure.Auth;
using FluentAssertions;

namespace DwbHub.Tests.Security;

public sealed class BCryptSecurityTests
{
    /// <summary>
    /// Verifies that the production hasher embeds a work factor of at least 12 in
    /// every produced hash string. This test catches a regression where the constant
    /// is accidentally lowered — a BCrypt hash encodes the cost factor in the string
    /// itself ($2a$NN$...), so it can be parsed directly.
    /// </summary>
    [Fact]
    public void BCryptPasswordHasher_ProducesHash_WithCostFactorAtLeast12()
    {
        var sut = new BCryptPasswordHasher();

        var hash = sut.Hash("verification-password-for-cost-check");

        // BCrypt hash format: $2a$12$<53-char-salt+hash>
        // Segment [2] after splitting on '$' is the numeric cost factor.
        var segments = hash.Split('$');
        segments.Should().HaveCountGreaterThanOrEqualTo(4,
            "a valid BCrypt hash contains at least 4 '$'-delimited segments");

        var costFactorParsed = int.TryParse(segments[2], out var costFactor);
        costFactorParsed.Should().BeTrue("segment [2] of a BCrypt hash must be a numeric work factor");
        costFactor.Should().BeGreaterThanOrEqualTo(12,
            "work factor below 12 is considered too weak for production password storage");
    }
}
