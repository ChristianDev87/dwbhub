namespace DwbHub.Application.Setup;

/// <summary>
/// Persists the bootstrap-token plaintext to wherever the operator can retrieve
/// it (filesystem in production, in-memory in tests). The provisioner calls
/// <see cref="WriteAsync"/> exactly once, when a fresh token is generated.
/// On successful wizard completion, <see cref="DeleteAsync"/> removes the file.
/// </summary>
public interface IBootstrapTokenWriter
{
    /// <summary>Returns the resolved location of the token (e.g. a file path), for log messages.</summary>
    string Location { get; }

    /// <summary>Persist the plaintext. Auto-creates parent directories as needed.</summary>
    Task WriteAsync(string plaintext, CancellationToken ct = default);

    /// <summary>Best-effort delete. Returns true if the location no longer exists after the call.</summary>
    Task<bool> DeleteAsync(CancellationToken ct = default);

    /// <summary>True if the persisted location currently has a token in it.</summary>
    Task<bool> ExistsAsync(CancellationToken ct = default);
}
