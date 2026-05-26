namespace DwbHub.Api.Controllers.Setup;

/// <summary>Response body for a successful POST /api/setup/complete (201 Created).</summary>
/// <param name="TenantId">Internal ID of the newly created tenant.</param>
/// <param name="TenantSlug">URL slug of the newly created tenant.</param>
/// <param name="OwnerUserId">Internal ID of the newly created owner user.</param>
/// <param name="VerificationEmailSent">
/// <c>true</c> when the verification email was queued successfully;
/// <c>false</c> when the SMTP send failed (setup still succeeded).
/// </param>
public sealed record SetupCompleteResponse(
    long TenantId,
    string TenantSlug,
    long OwnerUserId,
    bool VerificationEmailSent);
