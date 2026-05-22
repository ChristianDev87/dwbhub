namespace DwbHub.Core.Entities;

public sealed record VerifyResendResult(
    bool UserFound,
    bool AlreadyVerified,
    bool RateLimited,
    long? NewTokenId);
