namespace DwbHub.Api.Controllers.Setup;

public sealed record SetupCompleteResponse(
    long TenantId,
    string TenantSlug,
    long OwnerUserId,
    bool VerificationEmailSent);
