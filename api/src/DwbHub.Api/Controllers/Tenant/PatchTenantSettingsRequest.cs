namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// PATCH body for <c>/api/t/{slug}/settings</c>. Set <see cref="MessageEditWindowSeconds"/>
/// to NULL to disable the edit-window limit; otherwise 60–31536000 seconds.
/// </summary>
/// <param name="MessageEditWindowSeconds">NULL = unlimited; 60 = 1 minute; 31536000 = 1 year.</param>
public sealed record PatchTenantSettingsRequest(int? MessageEditWindowSeconds);
