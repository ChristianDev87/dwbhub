using System.Net;
using System.Security.Claims;
using DwbHub.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Auth;

[ApiController]
public sealed class AuthController(
    ILoginService loginService,
    IRefreshTokenService refreshTokenService,
    IEmailVerificationService emailVerificationService,
    IPasswordResetService passwordResetService) : ControllerBase
{
    private const string RefreshCookieName = "dwbhub_refresh";
    private const string RefreshCookiePath = "/api/auth";
    private static readonly TimeSpan RefreshCookieLifetime = TimeSpan.FromDays(30);

    [HttpPost("/api/tenants/{slug}/auth/login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login(string slug, [FromBody] LoginRequest body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress ?? IPAddress.None;
        var outcome = await loginService.LoginAsync(slug, body.Email, body.Password, ip, ct);

        return outcome switch
        {
            LoginOutcome.Success s => LoginSuccessResponse(s),
            LoginOutcome.LockedOut lo => LockedWithRetryAfter(lo.RetryAfterSeconds),
            LoginOutcome.EmailNotVerified ev => StatusCode(StatusCodes.Status403Forbidden,
                new { error = "email_not_verified", email = ev.Email }),
            LoginOutcome.InvalidCredentials => Unauthorized(new { error = "invalid_credentials" }),
            _ => Unauthorized(new { error = "invalid_credentials" }),
        };
    }

    [HttpPost("/api/auth/verify-email/resend")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmailResend([FromBody] VerifyEmailResendRequest body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        var ua = Request.Headers.UserAgent.ToString();
        var locale = ResolveLocale();
        await emailVerificationService.ResendAsync(body.TenantSlug, body.Email, locale, ip, ua, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("/api/auth/verify-email/confirm")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmailConfirm([FromBody] VerifyEmailConfirmRequest body, CancellationToken ct)
    {
        var outcome = await emailVerificationService.ConfirmAsync(body.Token, ct);
        return outcome switch
        {
            VerifyConfirmOutcome.Success => Ok(new { verified = true }),
            VerifyConfirmOutcome.Invalid => BadRequest(new { error = "invalid_or_expired_token" }),
            _ => BadRequest(new { error = "invalid_or_expired_token" }),
        };
    }

    [HttpPost("/api/auth/password-reset/request")]
    [AllowAnonymous]
    public async Task<IActionResult> PasswordResetRequest([FromBody] PasswordResetRequestRequest body, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress;
        var ua = Request.Headers.UserAgent.ToString();
        var locale = ResolveLocale();
        await passwordResetService.RequestAsync(body.TenantSlug, body.Email, locale, ip, ua, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("/api/auth/password-reset/confirm")]
    [AllowAnonymous]
    public async Task<IActionResult> PasswordResetConfirm([FromBody] PasswordResetConfirmRequest body, CancellationToken ct)
    {
        var outcome = await passwordResetService.ConfirmAsync(body.Token, body.NewPassword, ct);
        return outcome switch
        {
            ResetConfirmOutcome.Success s => Ok(new { reset = true, sessionsRevoked = s.SessionsRevoked }),
            ResetConfirmOutcome.WeakPassword => BadRequest(new { error = "weak_password", min_length = PasswordStrength.MinimumLength }),
            ResetConfirmOutcome.Invalid => BadRequest(new { error = "invalid_or_expired_token" }),
            _ => BadRequest(new { error = "invalid_or_expired_token" }),
        };
    }

    [HttpPost("/api/auth/refresh")]
    [AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (!Request.Cookies.TryGetValue(RefreshCookieName, out var refreshToken) || string.IsNullOrEmpty(refreshToken))
        {
            return Unauthorized(new { error = "no_refresh_token" });
        }

        var ip = HttpContext.Connection.RemoteIpAddress ?? IPAddress.None;
        var ua = Request.Headers.UserAgent.ToString();
        var outcome = await refreshTokenService.RefreshAsync(refreshToken, ip, ua, ct);

        return outcome switch
        {
            RefreshOutcome.Success s => SuccessResponse(s),
            RefreshOutcome.Invalid => UnauthorizedWithCookieCleared("invalid_refresh_token"),
            RefreshOutcome.RightsChanged => UnauthorizedWithCookieCleared("rights_changed"),
            RefreshOutcome.ChainCompromised => UnauthorizedWithCookieCleared("session_revoked"),
            _ => UnauthorizedWithCookieCleared("invalid_refresh_token"),
        };

        IActionResult SuccessResponse(RefreshOutcome.Success s)
        {
            SetRefreshCookie(s.RefreshToken);
            return Ok(new RefreshResponse(s.AccessToken));
        }

        IActionResult UnauthorizedWithCookieCleared(string error)
        {
            ClearRefreshCookie();
            return Unauthorized(new { error });
        }
    }

    [HttpPost("/api/auth/logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (Request.Cookies.TryGetValue(RefreshCookieName, out var refreshToken) && !string.IsNullOrEmpty(refreshToken))
        {
            await refreshTokenService.LogoutAsync(refreshToken, ct);
        }
        ClearRefreshCookie();
        return NoContent();
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

    private string ResolveLocale()
    {
        var header = Request.Headers.AcceptLanguage.ToString();
        if (header.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }
        return "de";
    }

    private IActionResult LoginSuccessResponse(LoginOutcome.Success s)
    {
        SetRefreshCookie(s.RefreshToken);
        return Ok(new LoginResponse(
            s.AccessToken,
            new LoginUserDto(s.User.Id, s.User.Email, s.User.DisplayName, s.User.Role.ToString()),
            new LoginTenantDto(s.Tenant.Id, s.Tenant.Slug, s.Tenant.Name)));
    }

    private ObjectResult LockedWithRetryAfter(int seconds)
    {
        Response.Headers.RetryAfter = seconds.ToString();
        return StatusCode(StatusCodes.Status423Locked,
            new { error = "locked", retry_after_seconds = seconds });
    }

    private void SetRefreshCookie(string plaintext)
    {
        Response.Cookies.Append(RefreshCookieName, plaintext, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            MaxAge = RefreshCookieLifetime,
        });
    }

    private void ClearRefreshCookie()
    {
        Response.Cookies.Append(RefreshCookieName, "", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            MaxAge = TimeSpan.Zero,
        });
    }
}
