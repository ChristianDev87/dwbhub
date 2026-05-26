using System.ComponentModel.DataAnnotations;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>
/// PATCH body for <c>/api/t/{slug}/channels/{channelPublicId}/messages/{messageId}</c>.
/// </summary>
public sealed record EditMessageRequest
{
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Content { get; init; } = "";
}
