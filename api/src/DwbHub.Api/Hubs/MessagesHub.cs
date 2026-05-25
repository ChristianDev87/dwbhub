using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DwbHub.Api.Hubs;

/// <summary>
/// Push-only SignalR hub at /api/hubs/messages.
///
/// Security-critical invariant: the tenant group is derived entirely from
/// the server-side JWT "tid" claim — the client has NO input into which
/// group it joins. Any future contributor adding a client-callable method
/// would bypass this invariant; the unit test Hub_HasNoClientCallableMethods
/// guards against that.
///
/// Because this endpoint is not prefixed with /api/t/{slug}/..., the
/// TenantResolverMiddleware does not run here. Tenant identification
/// therefore comes directly from the authenticated JWT claim "tid".
/// </summary>
[Authorize]
public sealed class MessagesHub : Hub
{
    private readonly ILogger<MessagesHub> _logger;

    public MessagesHub(ILogger<MessagesHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // SECURITY: tenant id comes from the JWT "tid" claim that the server
        // validated during authentication. The client cannot forge or alter this
        // value; any attempt to tamper the token would fail HMAC verification.
        var tidClaim = Context.User?.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tidClaim) || !long.TryParse(tidClaim, out var tenantId) || tenantId <= 0)
        {
            _logger.LogWarning(
                "SignalR connect with unresolved or invalid tenant claim — aborting connection {ConnId}",
                Context.ConnectionId);
            Context.Abort();
            return;
        }

        var group = $"tenant:{tenantId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);

        _logger.LogInformation(
            "SignalR connect: conn={ConnId} tenant={TenantId}",
            Context.ConnectionId, tenantId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "SignalR disconnect: conn={ConnId} reason={Reason}",
            Context.ConnectionId, exception?.Message ?? "clean");
        await base.OnDisconnectedAsync(exception);
    }

    // No client-callable methods. All events are server-pushed via IHubContext.
    // The unit test Hub_HasNoClientCallableMethods enforces this invariant.
}
