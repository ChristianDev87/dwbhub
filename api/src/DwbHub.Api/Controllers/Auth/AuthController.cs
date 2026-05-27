using System.Net;
using DwbHub.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DwbHub.Api.Controllers.Auth;

/// <summary>
/// Authentication endpoints: login, logout, token refresh, email verification,
/// and password reset. The refresh token is stored as an HttpOnly cookie;
/// the access token is returned in the response body.
/// </summary>
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

    /// <summary>
    /// Log in a user for a tenant identified by slug. Returns a JWT access token and sets the
    /// <c>dwbhub_refresh</c> HttpOnly cookie. Returns 423 Locked with a Retry-After header when
    /// the account is rate-limited, 403 when the email is not yet verified.
    /// </summary>
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

    /// <summary>
    /// Resend the email-verification message for a user. Always returns 200 OK regardless of
    /// whether the address is known, to avoid user enumeration.
    /// </summary>
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

    /// <summary>
    /// Confirm email verification using the one-time token from the verification email.
    /// Returns 400 when the token is invalid or expired.
    /// </summary>
    [HttpPost("/api/auth/verify-email/confirm")]
    [AllowAnonymous]
    [ProducesResponseType<VerifyEmailConfirmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> VerifyEmailConfirm([FromBody] VerifyEmailConfirmRequest body, CancellationToken ct)
    {
        var outcome = await emailVerificationService.ConfirmAsync(body.Token, ct);
        return outcome switch
        {
            VerifyConfirmOutcome.Success => Ok(new VerifyEmailConfirmResponse(Verified: true)),
            VerifyConfirmOutcome.Invalid => BadRequest(new { error = "invalid_or_expired_token" }),
            _ => BadRequest(new { error = "invalid_or_expired_token" }),
        };
    }

    /// <summary>
    /// Request a password-reset email. Always returns 200 OK regardless of whether the
    /// address is known, to avoid user enumeration.
    /// </summary>
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

    /// <summary>
    /// Confirm a password reset using the one-time token and the new password.
    /// Returns 400 for an invalid/expired token or a weak password.
    /// </summary>
    [HttpPost("/api/auth/password-reset/confirm")]
    [AllowAnonymous]
    [ProducesResponseType<PasswordResetConfirmResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> PasswordResetConfirm([FromBody] PasswordResetConfirmRequest body, CancellationToken ct)
    {
        var outcome = await passwordResetService.ConfirmAsync(body.Token, body.NewPassword, ct);
        return outcome switch
        {
            ResetConfirmOutcome.Success s => Ok(new PasswordResetConfirmResponse(Reset: true, SessionsRevoked: s.SessionsRevoked)),
            ResetConfirmOutcome.WeakPassword => BadRequest(new { error = "weak_password", min_length = PasswordStrength.MinimumLength }),
            ResetConfirmOutcome.Invalid => BadRequest(new { error = "invalid_or_expired_token" }),
            _ => BadRequest(new { error = "invalid_or_expired_token" }),
        };
    }

    /// <summary>
    /// Rotate the refresh token. Reads the <c>dwbhub_refresh</c> HttpOnly cookie, issues a new
    /// access token and rotates the cookie. Returns 401 and clears the cookie on any invalid,
    /// replayed, or rights-changed token.
    /// </summary>
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

    /// <summary>
    /// Log out the current session. Revokes the refresh token in the database and clears the
    /// <c>dwbhub_refresh</c> cookie. Always returns 204 No Content, even when no cookie is present.
    /// </summary>
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
