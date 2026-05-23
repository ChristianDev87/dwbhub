namespace DwbHub.Api.Controllers.Tenant;

public sealed record GuildResponse(
    Guid PublicId,
    string DiscordGuildId,
    string DisplayName,
    bool IsActive,
    DateTimeOffset RegisteredAt,
    bool BotCredentialsConfigured);
