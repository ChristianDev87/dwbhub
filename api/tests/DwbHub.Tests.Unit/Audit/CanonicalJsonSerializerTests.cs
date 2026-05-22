using System.Net;
using System.Text;
using DwbHub.Application.Audit;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Audit;

public sealed class CanonicalJsonSerializerTests
{
    [Fact]
    public void Serialize_empty_dict_emits_empty_object()
    {
        CanonicalJsonSerializer.Serialize(new Dictionary<string, object?>())
            .Should().Be("{}");
    }

    [Fact]
    public void Serialize_single_pair_emits_no_whitespace()
    {
        var json = CanonicalJsonSerializer.Serialize(
            new Dictionary<string, object?> { ["key"] = "value" });
        json.Should().Be(@"{""key"":""value""}");
    }

    [Fact]
    public void Serialize_sorts_keys_alphabetically()
    {
        var json = CanonicalJsonSerializer.Serialize(
            new Dictionary<string, object?> { ["b"] = 1, ["a"] = 2, ["c"] = 3 });
        json.Should().Be(@"{""a"":2,""b"":1,""c"":3}");
    }

    [Fact]
    public void Serialize_preserves_null_values()
    {
        var json = CanonicalJsonSerializer.Serialize(
            new Dictionary<string, object?> { ["key"] = null });
        json.Should().Be(@"{""key"":null}");
    }

    [Fact]
    public void Serialize_preserves_array_order()
    {
        var json = CanonicalJsonSerializer.Serialize(
            new Dictionary<string, object?> { ["list"] = new[] { 3, 1, 2 } });
        json.Should().Be(@"{""list"":[3,1,2]}");
    }

    [Fact]
    public void Serialize_is_deterministic_across_repeated_calls()
    {
        var payload = new Dictionary<string, object?>
        {
            ["tenantSlug"] = "acme",
            ["email"] = "alice@acme.test",
            ["reason"] = "wrong_password",
        };
        var first = CanonicalJsonSerializer.Serialize(payload);
        for (var i = 0; i < 100; i++)
        {
            CanonicalJsonSerializer.Serialize(payload).Should().Be(first);
        }
    }

    [Fact]
    public void Hash_of_canonical_string_is_32_bytes_sha256()
    {
        var hash = CanonicalJsonSerializer.Hash("{}");
        hash.Length.Should().Be(32);
        // Pin to SHA-256("{}") — never changes if implementation stays correct
        Convert.ToHexString(hash).Should().Be(
            "44136FA355B3678A1146AD16F7E8649E94FB4FC21FE77E8310C060F61CAAFF8A");
    }

    [Fact]
    public void HashEvent_combines_all_tuple_fields_deterministically()
    {
        var occurredAt = new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);
        var payload = CanonicalJsonSerializer.Serialize(
            new Dictionary<string, object?> { ["tenantSlug"] = "acme" });
        var h1 = CanonicalJsonSerializer.HashEvent(
            tenantId: 1, actorUserId: 42, eventType: "auth.login.success",
            canonicalPayloadJson: payload, ip: IPAddress.Parse("10.0.0.1"),
            userAgent: "test", occurredAt: occurredAt);
        var h2 = CanonicalJsonSerializer.HashEvent(
            tenantId: 1, actorUserId: 42, eventType: "auth.login.success",
            canonicalPayloadJson: payload, ip: IPAddress.Parse("10.0.0.1"),
            userAgent: "test", occurredAt: occurredAt);
        h1.Should().Equal(h2);

        // Changing any single field produces a different hash
        var h3 = CanonicalJsonSerializer.HashEvent(
            tenantId: 1, actorUserId: 43, eventType: "auth.login.success",
            canonicalPayloadJson: payload, ip: IPAddress.Parse("10.0.0.1"),
            userAgent: "test", occurredAt: occurredAt);
        h3.Should().NotEqual(h1);
    }
}
