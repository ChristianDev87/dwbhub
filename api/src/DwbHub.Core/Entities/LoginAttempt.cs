using System.Net;

namespace DwbHub.Core.Entities;

/// <summary>
/// One row in login_attempt_log. System-wide (no tenant scoping) because
/// rate-limiting per (email, ip) must hold across tenants.
/// </summary>
/// <param name="Id">Internal primary key.</param>
/// <param name="Email">Email address that was used in the attempt.</param>
/// <param name="IpAddress">Client IP address at the time of the attempt.</param>
/// <param name="Success">True iff the attempt resulted in a successful authentication.</param>
/// <param name="AttemptedAt">Timestamp of the attempt (TIMESTAMPTZ).</param>
public sealed record LoginAttempt(
    long Id,
    string Email,
    IPAddress IpAddress,
    bool Success,
    DateTimeOffset AttemptedAt);
