namespace DwbHub.Core.Entities;

/// <summary>
/// Role of a user within a tenant. Mirrors the CHECK constraint on users.role.
/// </summary>
public enum UserRole
{
    Owner,
    Admin,
    Moderator,
    Member,
    Guest,
}
