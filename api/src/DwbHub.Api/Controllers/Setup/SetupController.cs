using System.Net;
using DwbHub.Application.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Setup;

[ApiController]
public sealed class SetupController(ISetupService setupService) : ControllerBase
{
    [HttpGet("/api/setup/status")]
    [AllowAnonymous]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var status = await setupService.GetStatusAsync(ct);
        return Ok(new SetupStatusResponse(status.Completed, status.CompletedAt));
    }

    [HttpPost("/api/setup/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> Complete([FromBody] SetupCompleteRequest body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        var ua = Request.Headers.UserAgent.ToString();

        var outcome = await setupService.CompleteAsync(
            new SetupRequest(
                body.BootstrapToken,
                body.TenantName,
                body.TenantSlug,
                body.TenantLocale,
                body.OwnerEmail,
                body.OwnerDisplayName,
                body.OwnerPassword),
            ip, ua, ct);

        return outcome switch
        {
            SetupOutcome.Success s => StatusCode(StatusCodes.Status201Created,
                new SetupCompleteResponse(s.TenantId, s.TenantSlug, s.OwnerUserId, s.VerificationEmailSent)),

            SetupOutcome.InvalidToken => Unauthorized(new { error = "invalid_bootstrap_token" }),

            SetupOutcome.AlreadyCompleted => StatusCode(StatusCodes.Status410Gone,
                new { error = "setup_already_completed" }),

            SetupOutcome.WeakPassword => BadRequest(new { error = "weak_password", min_length = 8 }),

            SetupOutcome.SlugInUse => Conflict(new { error = "slug_in_use" }),

            SetupOutcome.InvalidRequest ir => BadRequest(new { error = "invalid_request", details = ir.Details }),

            _ => BadRequest(new { error = "invalid_request" }),
        };
    }
}
