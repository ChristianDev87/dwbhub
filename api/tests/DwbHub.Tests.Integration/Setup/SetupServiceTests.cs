using System.Net;
using Dapper;
using DwbHub.Application.Auth;
using DwbHub.Application.Setup;
using DwbHub.Core.Repositories;
using DwbHub.Data.Connections;
using DwbHub.Data.Repositories;
using DwbHub.Infrastructure.Auth;
using DwbHub.Infrastructure.Email;
using DwbHub.Tests.Integration.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace DwbHub.Tests.Integration.Setup;

[Collection(EmailCollection.Name)]
public sealed class SetupServiceTests : IAsyncLifetime
{
    private const string Base64Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private static readonly IPAddress TestIp = IPAddress.Parse("127.0.0.1");

    private readonly PostgresFixture _pg;
    private readonly MailpitFixture _mail;
    private readonly NpgsqlDataSource _ds;
    private readonly TenantRepository _tenants;
    private readonly UserRepository _users;
    private readonly SystemBootstrapLockRepository _locks;
    private readonly TokenHasher _tokenHasher;
    private readonly TokenGenerator _tokenGenerator;
    private readonly BCryptPasswordHasher _passwordHasher;
    private readonly InMemoryWriter _writer;
    private readonly EmailVerificationService _emailVerification;
    private readonly SetupService _sut;

    public SetupServiceTests(PostgresFixture pg, MailpitFixture mail)
    {
        _pg = pg;
        _mail = mail;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
        _ds = NpgsqlDataSource.Create(_pg.ConnectionString);
        var factory = new NpgsqlConnectionFactory(_ds);
        _tenants = new TenantRepository(factory);
        _users = new UserRepository(factory);
        _locks = new SystemBootstrapLockRepository(factory);
        _tokenHasher = new TokenHasher();
        _tokenGenerator = new TokenGenerator();
        _passwordHasher = new BCryptPasswordHasher();
        _writer = new InMemoryWriter();
        var renderer = new TemplateEmailRenderer();
        var sender = new MailKitEmailSender(_mail.SmtpHost, _mail.SmtpPort, "DwbHub <noreply@test.local>");
        var auditRepo = new DwbHub.Data.Repositories.AuditLogRepository(factory);
        var auditWriter = new DwbHub.Application.Audit.AuditWriter(auditRepo);
        _emailVerification = new EmailVerificationService(
            new AuthTokenRepository(factory), _tenants, _users,
            _tokenHasher, _tokenGenerator, renderer, sender,
            publicBaseUrl: "http://localhost:5173", auditWriter);
        _sut = new SetupService(
            _locks, _tenants, _users,
            _tokenHasher, _passwordHasher, _emailVerification, _writer,
            NullLogger<SetupService>.Instance, auditWriter);
    }

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await _mail.ResetAsync();
        _writer.Reset();
    }
    public Task DisposeAsync() { _ds.Dispose(); return Task.CompletedTask; }

    private async Task<string> SeedPendingLockAsync()
    {
        var plaintext = _tokenGenerator.GenerateUrlSafeBase64();
        await _locks.InsertAsync(_tokenHasher.Hash(plaintext));
        await _writer.WriteAsync(plaintext);
        return plaintext;
    }

    private static SetupRequest MakeRequest(string token, string slug = "acme") =>
        new(
            BootstrapToken: token,
            TenantName: "Acme Corp",
            TenantSlug: slug,
            TenantLocale: "de",
            OwnerEmail: "owner@acme.test",
            OwnerDisplayName: "Owner",
            OwnerPassword: "correct horse battery staple");

    [Fact]
    public async Task Status_returns_completed_false_when_no_lock_exists()
    {
        var status = await _sut.GetStatusAsync();
        status.Completed.Should().BeFalse();
        status.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Status_returns_completed_true_after_successful_complete()
    {
        var token = await SeedPendingLockAsync();
        _ = await _sut.CompleteAsync(MakeRequest(token), TestIp, "test-ua");

        var status = await _sut.GetStatusAsync();
        status.Completed.Should().BeTrue();
        status.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Complete_with_valid_token_creates_tenant_user_and_sends_email()
    {
        var token = await SeedPendingLockAsync();

        var outcome = await _sut.CompleteAsync(MakeRequest(token), TestIp, "test-ua");

        outcome.Should().BeOfType<SetupOutcome.Success>();
        var success = (SetupOutcome.Success)outcome;
        success.VerificationEmailSent.Should().BeTrue();
        success.TenantSlug.Should().Be("acme");

        var tenant = await _tenants.GetByIdAsync(success.TenantId);
        tenant!.Locale.Should().Be("de");

        var user = await _users.GetByIdAsync(success.TenantId, success.OwnerUserId);
        user!.EmailVerifiedAt.Should().BeNull();
        user.Role.Should().Be(DwbHub.Core.Entities.UserRole.Owner);

        var msgs = await _mail.GetMessagesAsync();
        msgs.Should().HaveCount(1);
        msgs[0].To[0].Address.Should().Be("owner@acme.test");

        (await _writer.ExistsAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_with_wrong_token_returns_InvalidToken()
    {
        await SeedPendingLockAsync();

        var outcome = await _sut.CompleteAsync(MakeRequest("not-the-right-token"), TestIp, null);

        outcome.Should().BeOfType<SetupOutcome.InvalidToken>();
        var listed = await _tenants.ListAsync();
        listed.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_after_consume_returns_AlreadyCompleted()
    {
        var token = await SeedPendingLockAsync();
        _ = await _sut.CompleteAsync(MakeRequest(token, slug: "first"), TestIp, null);

        var outcome = await _sut.CompleteAsync(MakeRequest(token, slug: "second"), TestIp, null);

        outcome.Should().BeOfType<SetupOutcome.AlreadyCompleted>();
    }

    [Fact]
    public async Task Complete_with_weak_password_returns_WeakPassword()
    {
        var token = await SeedPendingLockAsync();
        var req = MakeRequest(token) with { OwnerPassword = "short" };

        var outcome = await _sut.CompleteAsync(req, TestIp, null);

        outcome.Should().BeOfType<SetupOutcome.WeakPassword>();
    }

    [Fact]
    public async Task Complete_with_invalid_slug_returns_InvalidRequest()
    {
        var token = await SeedPendingLockAsync();
        var req = MakeRequest(token) with { TenantSlug = "Acme!Corp" };

        var outcome = await _sut.CompleteAsync(req, TestIp, null);

        outcome.Should().BeOfType<SetupOutcome.InvalidRequest>();
    }

    [Fact]
    public async Task Complete_with_duplicate_slug_returns_SlugInUse()
    {
        var token = await SeedPendingLockAsync();
        await _tenants.CreateAsync("Existing", "acme");

        var outcome = await _sut.CompleteAsync(MakeRequest(token), TestIp, null);

        outcome.Should().BeOfType<SetupOutcome.SlugInUse>();
    }

    [Fact]
    public async Task Setup_completed_emits_setup_completed_event()
    {
        var token = await SeedPendingLockAsync();
        var outcome = await _sut.CompleteAsync(MakeRequest(token), ip: null, userAgent: null);
        outcome.Should().BeOfType<SetupOutcome.Success>();

        await using var conn = _ds.CreateConnection();
        await conn.OpenAsync();
        var et = await conn.QuerySingleAsync<string?>(
            "SELECT event_type FROM audit_log WHERE event_type = 'setup.completed' LIMIT 1");
        et.Should().Be("setup.completed");
    }

    private sealed class InMemoryWriter : IBootstrapTokenWriter
    {
        private string? _plaintext;
        public string Location => "(in-memory)";
        public Task WriteAsync(string plaintext, CancellationToken ct = default)
        {
            _plaintext = plaintext;
            return Task.CompletedTask;
        }
        public Task<bool> DeleteAsync(CancellationToken ct = default)
        {
            _plaintext = null;
            return Task.FromResult(true);
        }
        public Task<bool> ExistsAsync(CancellationToken ct = default)
            => Task.FromResult(_plaintext is not null);
        public void Reset() => _plaintext = null;
    }
}
