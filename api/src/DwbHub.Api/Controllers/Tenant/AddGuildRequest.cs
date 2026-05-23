using System.ComponentModel.DataAnnotations;

namespace DwbHub.Api.Controllers.Tenant;

public sealed class AddGuildRequest
{
    [Required]
    [RegularExpression(@"^\d{17,20}$", ErrorMessage = "invalid_discord_guild_id")]
    public string DiscordGuildId { get; init; } = "";

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; init; } = "";
}
