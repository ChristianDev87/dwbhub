namespace DwbHub.Core.Entities;

public sealed record VerifyConfirmResult(
    bool TokenFound,
    bool? NotConsumed,
    bool? NotExpired,
    bool Verified);
