namespace DwbHub.Api.Controllers.Setup;

public sealed record SetupCompleteRequest(
    string BootstrapToken,
    string TenantName,
    string TenantSlug,
    string TenantLocale,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);
