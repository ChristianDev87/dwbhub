namespace DwbHub.Infrastructure.Logging;

/// <summary>
/// Creates a per-start log file inside the given directory and prunes old files to a retention count.
/// </summary>
public static class PerStartFileLogger
{
    public const string FilePrefix = "dwbhub-";
    public const string FileSuffix = ".log";

    public static string Initialize(string logDirectory, int retainStartLogs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainStartLogs, 1);

        Directory.CreateDirectory(logDirectory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var fileName = $"{FilePrefix}{timestamp}{FileSuffix}";
        var filePath = Path.Combine(logDirectory, fileName);

        // File.Create overwrites silently on collision. Two callers in the same UTC second
        // would share one empty log file, which interleaves their entries but does not lose
        // data. Acceptable for now — see Plan 0.6 if multi-process operator scenarios become
        // real.
        // Touch the file so callers can rely on its existence.
        using (File.Create(filePath))
        {
        }

        PruneOldStartLogs(logDirectory, retainStartLogs);

        return filePath;
    }

    private static void PruneOldStartLogs(string logDirectory, int retainStartLogs)
    {
        var pattern = $"{FilePrefix}*{FileSuffix}";
        var files = Directory.GetFiles(logDirectory, pattern)
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files.Skip(retainStartLogs))
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another process may still hold the file; skip silently. (Once Serilog
                // is wired the actual prior log file is owned by us with FileShare.Read
                // so this is mostly a safety net for concurrent operators.)
            }
        }
    }
}
