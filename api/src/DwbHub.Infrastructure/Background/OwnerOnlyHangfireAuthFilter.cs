using Hangfire.Dashboard;

namespace DwbHub.Infrastructure.Background;

/// <summary>
/// Hangfire dashboard authorization filter. Permits only authenticated users
/// holding the "Owner" role claim. Roles are stored in the "role" claim (NOT
/// ClaimTypes.Role) per Plan 0.3a JwtIssuer convention.
/// </summary>
public sealed class OwnerOnlyHangfireAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var user = http.User;
        if (user?.Identity?.IsAuthenticated != true) return false;

        // Plan 0.3a JwtIssuer writes the role into a plain "role" claim.
        var role = user.FindFirst("role")?.Value;
        return string.Equals(role, "Owner", StringComparison.Ordinal);
    }
}
