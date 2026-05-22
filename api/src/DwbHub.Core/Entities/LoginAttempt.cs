using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row in login_attempt_log. System-wide (no tenant scoping) because
/// rate-limiting per (email, ip) must hold across tenants.
/// </summary>
public sealed record LoginAttempt(
    long Id,
    string Email,
    IPAddress IpAddress,
    bool Success,
    DateTimeOffset AttemptedAt);
