using System.Net;
using System.Security.Claims;
using DwbHub.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Auth;

[ApiController]
public sealed class AuthController(ILoginService loginService) : ControllerBase
{
    [HttpPost("/api/tenants/{slug}/auth/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string slug, [FromBody] LoginRequest body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress ?? IPAddress.None;
        var outcome = await loginService.LoginAsync(slug, body.Email, body.Password, ip, ct);

        return outcome switch
        {
            LoginOutcome.Success s => Ok(new LoginResponse(
                s.AccessToken,
                new LoginUserDto(s.User.Id, s.User.Email, s.User.DisplayName, s.User.Role.ToString()),
                new LoginTenantDto(s.Tenant.Id, s.Tenant.Slug, s.Tenant.Name))),

            LoginOutcome.LockedOut lo => LockedWithRetryAfter(lo.RetryAfterSeconds),

            LoginOutcome.InvalidCredentials => Unauthorized(new { error = "invalid_credentials" }),

            _ => Unauthorized(new { error = "invalid_credentials" }),
        };
    }

    [HttpGet("/api/auth/me")]
    [Authorize]
    public IActionResult Me()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        var tid = User.FindFirstValue("tid");
        var tslug = User.FindFirstValue("tslug") ?? "";
        var role = User.FindFirstValue("role") ?? "";

        if (sub is null || tid is null)
        {
            return Unauthorized(new { error = "invalid_token" });
        }

        return Ok(new MeResponse(
            UserId: long.Parse(sub),
            TenantId: long.Parse(tid),
            TenantSlug: tslug,
            Role: role));
    }

    private ObjectResult LockedWithRetryAfter(int seconds)
    {
        Response.Headers.RetryAfter = seconds.ToString();
        return StatusCode(StatusCodes.Status423Locked,
            new { error = "locked", retry_after_seconds = seconds });
    }
}
