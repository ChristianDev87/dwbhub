namespace DwbHub.Api.Controllers.Tenant;

/// <summary>Response body for GET /api/t/{slug}/guilds.</summary>
/// <param name="Guilds">All guilds registered for the tenant, with bot status.</param>
public sealed record GuildListResponse(IReadOnlyList<GuildResponse> Guilds);
