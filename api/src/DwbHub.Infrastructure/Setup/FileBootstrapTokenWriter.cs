using DwbHub.Application.Setup;

namespace DwbHub.Infrastructure.Setup;

/// <summary>
/// Writes the bootstrap-token plaintext to a configurable file path on a mounted
/// Docker volume. Default path is /data/dwbhub/bootstrap-token.txt — operators
/// retrieve the token via `docker exec cat ...` or host-filesystem inspection.
/// On non-Unix hosts, file permission setting is best-effort.
/// </summary>
public sealed class FileBootstrapTokenWriter(string filePath) : IBootstrapTokenWriter
{
    public string Location => filePath;

    public async Task WriteAsync(string plaintext, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(filePath, plaintext, ct).ConfigureAwait(false);
        TrySetUnixOwnerOnlyPermissions(filePath);
    }

    public Task<bool> DeleteAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            return Task.FromResult(!File.Exists(filePath));
        }
        catch (IOException)
        {
            // Best-effort: the DB lock row is the source of truth, not the file.
            return Task.FromResult(false);
        }
    }

    public Task<bool> ExistsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(File.Exists(filePath));
    }

    private static void TrySetUnixOwnerOnlyPermissions(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // Windows: no chmod analogue worth doing.
        }
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        }
        catch
        {
            // Permission tightening is hygiene, not security-critical (the file
            // sits on a private volume already). Swallow any platform error.
        }
    }
}
