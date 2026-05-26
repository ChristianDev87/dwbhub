namespace DwbHub.Core.Entities;

/// <summary>
/// Singleton row that gates the first-time setup wizard. Created by the API on
/// first boot with a fresh SHA-256 hash; the plaintext token is written to the
/// volume file. The setup endpoint atomically consumes the row via UPDATE
/// WHERE consumed_at IS NULL — race-safe under concurrent submissions.
/// Operator can reset via DELETE + container restart.
/// </summary>
/// <param name="Id">Internal primary key (always 1 in practice — singleton row).</param>
/// <param name="TokenHash">SHA-256 hash of the plaintext bootstrap token written to the volume file.</param>
/// <param name="IssuedAt">Timestamp when the lock row was first created.</param>
/// <param name="ConsumedAt">Timestamp when the setup wizard was completed, or null if still active.</param>
/// <param name="ConsumedByUserId">ID of the user created during setup, or null if not yet consumed.</param>
public sealed record SystemBootstrapLock(
    long Id,
    byte[] TokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ConsumedAt,
    long? ConsumedByUserId);
