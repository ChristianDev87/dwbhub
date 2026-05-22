using DwbHub.Infrastructure.Auth;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Auth;

public sealed class BCryptPasswordHasherTests
{
    private readonly BCryptPasswordHasher _sut = new();

    [Fact]
    public void Hash_uses_random_salt()
    {
        var h1 = _sut.Hash("hunter2");
        var h2 = _sut.Hash("hunter2");
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void Verify_accepts_matching_password()
    {
        var hash = _sut.Hash("correct horse battery staple");
        _sut.Verify("correct horse battery staple", hash).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hash = _sut.Hash("correct horse battery staple");
        _sut.Verify("wrong horse battery staple", hash).Should().BeFalse();
    }
}
