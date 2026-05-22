using System.Net;
using System.Security.Claims;
using DwbHub.Application.Audit;
using DwbHub.Application.Tenancy;
using DwbHub.Core.Entities;
using DwbHub.Core.Repositories;
using DwbHub.Infrastructure.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace DwbHub.Tests.Unit.Tenancy;

public sealed class TenantResolverMiddlewareTests
{
    private static Tenant AcmeTenant() => new(
        Id: 42, Name: "Acme", Slug: "acme", Locale: "de",
        CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    private static TestServer BuildServer(
        Mock<ITenantRepository> tenants,
        Mock<IAuditWriter> auditWriter,
        Mock<IGuildRepository>? guilds = null,
        long? jwtTid = null,
        long? jwtSub = null)
    {
        guilds ??= new Mock<IGuildRepository>(MockBehavior.Loose);
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<ITenantRepository>(tenants.Object);
                    services.AddSingleton<IGuildRepository>(guilds.Object);
                    services.AddSingleton<IAuditWriter>(auditWriter.Object);
                    services.AddScoped<ITenantContext, TenantContext>();
                    services.AddScoped<IGuildContext, GuildContext>();
                    services.AddAuthentication("Test")
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                    services.AddRouting();
                    services.AddSingleton(new TestAuthState(jwtTid, jwtSub));
                });
                webBuilder.Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseMiddleware<TenantResolverMiddleware>();
                    app.Run(async ctx =>
                    {
                        var tctx = ctx.RequestServices.GetRequiredService<ITenantContext>();
                        var gctx = ctx.RequestServices.GetRequiredService<IGuildContext>();
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync(
                            $"OK,t={tctx.Current?.Id},g={gctx.Current?.PublicId}");
                    });
                });
            })
            .Build();

        host.Start();
        return host.GetTestServer();
    }

    private static Guild AcmeGuild(long tenantId, Guid pid) => new(
        Id: 7, PublicId: pid, TenantId: tenantId,
        DiscordGuildId: "1234567890123456789", DisplayName: "Production",
        IsActive: true, RegisteredByUserId: 99,
        RegisteredAt: DateTimeOffset.UtcNow, LastConnectedAt: null,
        CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task Path_without_tenant_prefix_passes_through()
    {
        var tenants = new Mock<ITenantRepository>(MockBehavior.Strict);
        var audit = new Mock<IAuditWriter>(MockBehavior.Strict);
        using var server = BuildServer(tenants, audit);
        var client = server.CreateClient();

        var res = await client.GetAsync("/api/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("t=,g=");
        tenants.VerifyNoOtherCalls();
        audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Valid_slug_without_jwt_passes_through_and_sets_context()
    {
        var tenants = new Mock<ITenantRepository>(MockBehavior.Strict);
        var audit = new Mock<IAuditWriter>(MockBehavior.Strict);
        tenants.Setup(r => r.GetBySlugAsync("acme", It.IsAny<CancellationToken>()))
               .ReturnsAsync(AcmeTenant());

        using var server = BuildServer(tenants, audit);
        var client = server.CreateClient();

        var res = await client.GetAsync("/api/t/acme/dashboard");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("t=42");
    }

    [Fact]
    public async Task Cross_tenant_mismatch_emits_403_and_audit_event()
    {
        var tenants = new Mock<ITenantRepository>(MockBehavior.Strict);
        var audit = new Mock<IAuditWriter>();
        tenants.Setup(r => r.GetBySlugAsync("acme", It.IsAny<CancellationToken>()))
               .ReturnsAsync(AcmeTenant());

        using var server = BuildServer(tenants, audit, jwtTid: 99, jwtSub: 7);
        var client = server.CreateClient();

        var res = await client.GetAsync("/api/t/acme/dashboard");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("cross_tenant_access_denied");

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e =>
                e.EventType == "auth.cross_tenant_access_blocked"
                && e.TenantId == 42L
                && e.ActorUserId == 7L
                && e.Payload.ContainsKey("pathSlug")
                && e.Payload.ContainsKey("pathTenantId")
                && e.Payload.ContainsKey("jwtTenantId")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unknown_slug_emits_404_and_audit_event()
    {
        var tenants = new Mock<ITenantRepository>(MockBehavior.Strict);
        var audit = new Mock<IAuditWriter>();
        tenants.Setup(r => r.GetBySlugAsync("nope", It.IsAny<CancellationToken>()))
               .ReturnsAsync((Tenant?)null);

        using var server = BuildServer(tenants, audit);
        var client = server.CreateClient();

        var res = await client.GetAsync("/api/t/nope/dashboard");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await res.Content.ReadAsStringAsync()).Should().Contain("tenant_not_found");

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e =>
                e.EventType == "auth.unknown_tenant_access"
                && e.TenantId == null
                && e.Payload.ContainsKey("pathSlug")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Guild_scoped_path_with_matching_jwt_populates_both_contexts()
    {
        var tenant = AcmeTenant();
        var pid = Guid.NewGuid();
        var guild = AcmeGuild(tenant.Id, pid);

        var tenants = new Mock<ITenantRepository>(MockBehavior.Loose);
        var guilds = new Mock<IGuildRepository>(MockBehavior.Strict);
        guilds.Setup(r => r.ResolveTenantAndGuildAsync("acme", pid, It.IsAny<CancellationToken>()))
              .ReturnsAsync((tenant, guild));
        var audit = new Mock<IAuditWriter>(MockBehavior.Strict);

        using var server = BuildServer(tenants, audit, guilds, jwtTid: tenant.Id, jwtSub: 1);
        var client = server.CreateClient();

        var res = await client.GetAsync($"/api/t/acme/g/{pid:D}/anything");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain($"t={tenant.Id}");
        body.Should().Contain($"g={pid}");
    }

    [Fact]
    public async Task Guild_scoped_path_with_unknown_guild_returns_404_and_emits_audit()
    {
        var tenant = AcmeTenant();
        var pid = Guid.NewGuid();

        var tenants = new Mock<ITenantRepository>(MockBehavior.Loose);
        var guilds = new Mock<IGuildRepository>(MockBehavior.Strict);
        guilds.Setup(r => r.ResolveTenantAndGuildAsync("acme", pid, It.IsAny<CancellationToken>()))
              .ReturnsAsync((tenant, (Guild?)null));
        var audit = new Mock<IAuditWriter>();

        using var server = BuildServer(tenants, audit, guilds, jwtTid: tenant.Id, jwtSub: 1);
        var client = server.CreateClient();

        var res = await client.GetAsync($"/api/t/acme/g/{pid:D}/anything");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await res.Content.ReadAsStringAsync()).Should().Contain("guild_not_found");

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e =>
                e.EventType == "guild.unknown_access"
                && e.TenantId == tenant.Id
                && e.Payload.ContainsKey("attemptedPublicId")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Guild_scoped_path_for_unknown_tenant_returns_404_and_emits_tenant_audit()
    {
        var pid = Guid.NewGuid();

        var tenants = new Mock<ITenantRepository>(MockBehavior.Loose);
        var guilds = new Mock<IGuildRepository>(MockBehavior.Strict);
        guilds.Setup(r => r.ResolveTenantAndGuildAsync("nope", pid, It.IsAny<CancellationToken>()))
              .ReturnsAsync(((Tenant?)null, (Guild?)null));
        var audit = new Mock<IAuditWriter>();

        using var server = BuildServer(tenants, audit, guilds);
        var client = server.CreateClient();

        var res = await client.GetAsync($"/api/t/nope/g/{pid:D}/anything");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await res.Content.ReadAsStringAsync()).Should().Contain("tenant_not_found");

        audit.Verify(a => a.RecordAsync(
            It.Is<AuditEvent>(e => e.EventType == "auth.unknown_tenant_access"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}

internal sealed record TestAuthState(long? Tid, long? Sub);

internal sealed class TestAuthHandler(
    Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
    Microsoft.Extensions.Logging.ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    TestAuthState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (state.Tid is null) return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new("tid", state.Tid.Value.ToString()) };
        if (state.Sub is long s) claims.Add(new Claim("sub", s.ToString()));
        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "Test");
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
