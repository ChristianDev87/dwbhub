namespace DwbHub.Api.Controllers.Setup;

/// <summary>Request body for POST /api/setup/complete.</summary>
/// <param name="BootstrapToken">One-time token written to disk by the provisioner at startup.</param>
/// <param name="TenantName">Human-readable name for the new tenant.</param>
/// <param name="TenantSlug">URL slug for the new tenant. Must be URL-safe and unique.</param>
/// <param name="TenantLocale">BCP-47 locale tag (e.g. <c>de</c>, <c>en</c>) for the tenant.</param>
/// <param name="OwnerEmail">Email address for the initial owner account.</param>
/// <param name="OwnerDisplayName">Display name for the initial owner account.</param>
/// <param name="OwnerPassword">Password for the initial owner account. Transmitted over TLS only.</param>
public sealed record SetupCompleteRequest(
    string BootstrapToken,
    string TenantName,
    string TenantSlug,
    string TenantLocale,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);
