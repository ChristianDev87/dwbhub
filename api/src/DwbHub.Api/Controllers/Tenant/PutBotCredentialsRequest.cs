using System.ComponentModel.DataAnnotations;
using DwbHub.Application.Validation;

namespace DwbHub.Api.Controllers.Tenant;

public sealed class PutBotCredentialsRequest
{
    [Required]
    [RegularExpression(@"^[A-Za-z0-9._-]{50,200}$", ErrorMessage = "invalid_bot_token_format")]
    [BotTokenShape]
    public string Token { get; init; } = "";
}
