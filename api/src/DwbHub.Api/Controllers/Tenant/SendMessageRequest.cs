using System.ComponentModel.DataAnnotations;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// Request body for POST /api/t/{slug}/channels/{channelPublicId}/messages.
/// </summary>
public sealed record SendMessageRequest
{
    [Required(AllowEmptyStrings = false, ErrorMessage = "content_required")]
    [StringLength(2000, MinimumLength = 1, ErrorMessage = "content_length_2000")]
    public string Content { get; init; } = "";
}
