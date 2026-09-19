using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class DiagnosticArchiveRetentionTests
{
    [Fact]
    public void Prune_RetainsNewestManagedArchivesAndRemovesOldExcess()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var oldest = CreateArchive(temp.Path, "20260901-010101", -10);
        var middle = CreateArchive(temp.Path, "20260902-010101", -9);
        var newest = CreateArchive(temp.Path, "20260903-010101", -8);

        var result = DiagnosticArchiveRetention.Prune(
            temp.Path,
            maximumRetained: 2,
            minimumEvidenceAge: TimeSpan.Zero,
            now: DateTimeOffset.UtcNow);

        Assert.True(result.Success);
        Assert.Equal(new[] { oldest }, result.RemovedPaths);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Prune_PreservesRecentAndUnknownDiagnosticFiles()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var recent = CreateArchive(temp.Path, "20260903-010101", 0);
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddMinutes(-1));
        var unknown = Path.Combine(temp.Path, "manual-diagnostic.zip");
        File.WriteAllText(unknown, "keep");

        var result = DiagnosticArchiveRetention.Prune(
            temp.Path,
            maximumRetained: 0,
            minimumEvidenceAge: TimeSpan.FromHours(2),
            now: DateTimeOffset.UtcNow);

        Assert.True(result.Success);
        Assert.Empty(result.RemovedPaths);
        Assert.Contains(recent, result.RetainedPaths);
        Assert.Contains(unknown, result.SkippedPaths);
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Prune_RejectsInvalidPolicyWithoutDeletingArchives()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var archive = CreateArchive(temp.Path, "20260901-010101", -10);

        var result = DiagnosticArchiveRetention.Prune(temp.Path, maximumRetained: -1);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.True(File.Exists(archive));
    }

    private static string CreateArchive(string directory, string timestamp, int ageDays)
    {
        var path = Path.Combine(directory, "dsh-desktop-diagnostics-" + timestamp + ".zip");
        File.WriteAllText(path, "diagnostic");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(ageDays));
        return path;
    }
}
