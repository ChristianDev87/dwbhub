using DwbHub.Application.Auth;
using DwbHub.Application.Setup;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using DwbHub.Infrastructure.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DwbHub.Tests.Unit.Setup;

public sealed class BootstrapTokenProvisionerTests
{
    [Fact]
    public async Task Provisions_fresh_token_when_no_lock_row_exists()
    {
        var locks = new InMemoryLockRepo(loaded: null);
        var writer = new InMemoryWriter();
        var sut = NewSut(locks, writer);

        await sut.ProvisionAsync();

        locks.InsertedHash.Should().NotBeNull().And.HaveCount(32);
        writer.WrittenPlaintext.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Skips_when_lock_already_consumed()
    {
        var consumed = new SystemBootstrapLock(
            Id: 1, TokenHash: new byte[32],
            IssuedAt: DateTimeOffset.UtcNow.AddDays(-1),
            ConsumedAt: DateTimeOffset.UtcNow.AddHours(-1),
            ConsumedByUserId: 42);
        var locks = new InMemoryLockRepo(loaded: consumed);
        var writer = new InMemoryWriter();
        var sut = NewSut(locks, writer);

        await sut.ProvisionAsync();

        locks.InsertedHash.Should().BeNull();
        writer.WrittenPlaintext.Should().BeNull();
    }

    [Fact]
    public async Task Skips_when_lock_pending_and_file_present()
    {
        var pending = new SystemBootstrapLock(
            Id: 1, TokenHash: new byte[32],
            IssuedAt: DateTimeOffset.UtcNow,
            ConsumedAt: null, ConsumedByUserId: null);
        var locks = new InMemoryLockRepo(loaded: pending);
        var writer = new InMemoryWriter(existsResult: true);
        var sut = NewSut(locks, writer);

        await sut.ProvisionAsync();

        locks.InsertedHash.Should().BeNull();
        writer.WrittenPlaintext.Should().BeNull();
    }

    private static BootstrapTokenProvisioner NewSut(InMemoryLockRepo locks, InMemoryWriter writer)
        => new(locks, new TokenHasher(), new TokenGenerator(), writer,
            NullLogger<BootstrapTokenProvisioner>.Instance);

    private sealed class InMemoryLockRepo(SystemBootstrapLock? loaded) : ISystemBootstrapLockRepository
    {
        public byte[]? InsertedHash { get; private set; }
        public Task<SystemBootstrapLock?> LoadAsync(CancellationToken ct = default) => Task.FromResult(loaded);
        public Task InsertAsync(byte[] tokenHash, CancellationToken ct = default)
        {
            InsertedHash = tokenHash;
            return Task.CompletedTask;
        }
        public Task<bool> TryConsumeAsync(byte[] tokenHash, long consumedByUserId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class InMemoryWriter(bool existsResult = false) : IBootstrapTokenWriter
    {
        public string Location => "(in-memory)";
        public string? WrittenPlaintext { get; private set; }
        public Task WriteAsync(string plaintext, CancellationToken ct = default)
        {
            WrittenPlaintext = plaintext;
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExistsAsync(CancellationToken ct = default) => Task.FromResult(existsResult);
    }
}
