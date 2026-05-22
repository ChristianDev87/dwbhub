namespace DwbHub.Core.Entities;

/// <summary>
/// Singleton row that gates the first-time setup wizard. Created by the API on
/// first boot with a fresh SHA-256 hash; the plaintext token is written to the
/// volume file. The setup endpoint atomically consumes the row via UPDATE
/// WHERE consumed_at IS NULL — race-safe under concurrent submissions.
/// Operator can reset via DELETE + container restart.
/// </summary>
public sealed record SystemBootstrapLock(
    long Id,
    byte[] TokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ConsumedAt,
    long? ConsumedByUserId);
