namespace DwbHub.Core.Entities;

public sealed record ResetRequestResult(
    bool UserFound,
    bool EmailNotVerified,
    bool RateLimited,
    long? NewTokenId);
