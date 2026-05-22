namespace DwbHub.Application.Setup;

public sealed record SetupRequest(
    string BootstrapToken,
    string TenantName,
    string TenantSlug,
    string TenantLocale,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);
