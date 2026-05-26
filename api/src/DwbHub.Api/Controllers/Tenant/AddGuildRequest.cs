using System.ComponentModel.DataAnnotations;

namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Request body for POST /api/t/{slug}/guilds. Registers a new Discord guild for the tenant.</summary>
public sealed class AddGuildRequest
{
    [Required]
    [RegularExpression(@"^\d{17,20}$", ErrorMessage = "invalid_discord_guild_id")]
    public string DiscordGuildId { get; init; } = "";

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; init; } = "";
}
