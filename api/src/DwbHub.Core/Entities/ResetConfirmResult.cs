namespace DwbHub.Core.Entities;

public sealed record ResetConfirmResult(
    bool TokenFound,
    bool? NotConsumed,
    bool? NotExpired,
    bool Updated,
    int SessionsRevoked);
