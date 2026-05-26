namespace DwbHub.Application.Setup;

/// <summary>Input for the one-time setup wizard.</summary>
/// <param name="BootstrapToken">Plaintext token from the bootstrap-token file; consumed once.</param>
/// <param name="TenantName">Human-readable display name for the new tenant.</param>
/// <param name="TenantSlug">URL-safe slug for the tenant; must match <c>^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$</c>.</param>
/// <param name="TenantLocale">Default locale for tenant emails; must be <c>de</c> or <c>en</c>.</param>
/// <param name="OwnerEmail">Email address of the first owner user.</param>
/// <param name="OwnerDisplayName">Display name of the first owner user.</param>
/// <param name="OwnerPassword">Plaintext password for the first owner user; must meet minimum strength.</param>
public sealed record SetupRequest(
    string BootstrapToken,
    string TenantName,
    string TenantSlug,
    string TenantLocale,
    string OwnerEmail,
    string OwnerDisplayName,
    string OwnerPassword);
