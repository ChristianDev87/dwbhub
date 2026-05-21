using DwbHub.Infrastructure.Logging;
using FluentAssertions;
using Xunit;

namespace DwbHub.Tests.Unit.Logging;

public sealed class PerStartFileLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public PerStartFileLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dwbhub-logtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Initialize_CreatesNewLogFileWithUtcTimestampedName()
    {
        var path = PerStartFileLogger.Initialize(_tempDir, retainStartLogs: 10);

        Path.GetFileName(path).Should().MatchRegex(@"^dwbhub-\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}\.log$");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Initialize_RetainsOnlyMostRecentStartLogs()
    {
        // seed 12 old log files with descending timestamps
        for (var i = 12; i >= 1; i--)
        {
            var name = $"dwbhub-2026-01-{i:D2}_10-00-00.log";
            File.WriteAllText(Path.Combine(_tempDir, name), $"old log #{i}");
        }

        PerStartFileLogger.Initialize(_tempDir, retainStartLogs: 10);

        var remaining = Directory.GetFiles(_tempDir, "dwbhub-*.log").Length;
        // 10 retained + 1 freshly created = 11 maximum
        remaining.Should().BeLessThanOrEqualTo(11);
        remaining.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public void Initialize_LeavesNonMatchingFilesUntouched()
    {
        File.WriteAllText(Path.Combine(_tempDir, "unrelated.txt"), "stay");
        File.WriteAllText(Path.Combine(_tempDir, "audit.log"), "stay");

        PerStartFileLogger.Initialize(_tempDir, retainStartLogs: 1);

        File.Exists(Path.Combine(_tempDir, "unrelated.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "audit.log")).Should().BeTrue();
    }

    [Fact]
    public void Initialize_HandlesEmptyDirectory()
    {
        var path = PerStartFileLogger.Initialize(_tempDir, retainStartLogs: 10);
        File.Exists(path).Should().BeTrue();
        Directory.GetFiles(_tempDir, "dwbhub-*.log").Should().HaveCount(1);
    }
}
