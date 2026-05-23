namespace DwbHub.Api.Controllers.Tenant;

public sealed record GuildListResponse(IReadOnlyList<GuildResponse> Guilds);
