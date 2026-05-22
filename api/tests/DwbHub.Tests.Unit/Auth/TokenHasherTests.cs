using DwbHub.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Auth;

public sealed class TokenHasherTests
{
    private readonly TokenHasher _sut = new();

    [Fact]
    public void Hash_yields_32_bytes_for_any_input()
    {
        _sut.Hash("").Should().HaveCount(32);
        _sut.Hash("a").Should().HaveCount(32);
        _sut.Hash(new string('x', 10_000)).Should().HaveCount(32);
    }

    [Fact]
    public void Hash_is_deterministic()
    {
        var input = "some-refresh-token-plaintext";
        var first = _sut.Hash(input);
        var second = _sut.Hash(input);
        first.Should().Equal(second);
    }
}
