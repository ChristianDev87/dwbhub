using System.Text.Json.Serialization;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Request body for PATCH /api/t/{slug}/settings.
/// All fields are optional; omitting a field leaves that setting unchanged.
/// Pass <c>null</c> explicitly to reset a setting to the system default.
/// </summary>
/// <param name="MessageEditWindowSeconds">
/// Per-tenant edit-window override in seconds, or <c>null</c> to reset to the system default.
/// Must be in the range [60, 31536000] when non-null.
/// </param>
public sealed record PatchTenantSettingsRequest(
    [property: JsonPropertyName("messageEditWindowSeconds")]
    int? MessageEditWindowSeconds);
