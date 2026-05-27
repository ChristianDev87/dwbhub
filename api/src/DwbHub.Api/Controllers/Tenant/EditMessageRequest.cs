using System.ComponentModel.DataAnnotations;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Request body for PATCH /api/t/{slug}/channels/{channelPublicId}/messages/{messagePublicId}.
/// </summary>
public sealed record EditMessageRequest
{
    /// <summary>
    /// New message content. Must be 1–1800 characters, non-whitespace-only.
    /// (Edit limit is intentionally stricter than Send's 2000-char limit.)
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "content_required")]
    [StringLength(1800, MinimumLength = 1, ErrorMessage = "content_length_1800")]
    public string Content { get; init; } = "";
}
