using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ProfileBackupRetentionTests
{
    [Fact]
    public void Prune_RetainsNewestPerManifestAndRemovesOlderBackups()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var first = CreateBackup(temp.Path, "package.json", 3, -3);
        var second = CreateBackup(temp.Path, "package.json", 2, -2);
        var newest = CreateBackup(temp.Path, "package.json", 1, -1);

        var result = ProfileBackupRetention.Prune(
            temp.Path,
            ["package.json"],
            maximumRetainedPerManifest: 2,
            minimumEvidenceAge: TimeSpan.Zero,
            now: DateTimeOffset.UtcNow);

        Assert.True(result.Success);
        Assert.Equal(new[] { newest, second }, result.RetainedFiles);
        Assert.Equal(new[] { first }, result.RemovedFiles);
        Assert.False(File.Exists(first));
    }

    [Fact]
    public void Prune_PreservesRecentEvidenceAndUnknownFiles()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var recent = CreateBackup(temp.Path, "package.json", 1, -1);
        var unknown = Path.Combine(temp.Path, "user-note.bak-20260919-000000");
        File.WriteAllText(unknown, "keep");

        var result = ProfileBackupRetention.Prune(
            temp.Path,
            ["package.json"],
            maximumRetainedPerManifest: 0,
            minimumEvidenceAge: TimeSpan.FromHours(1),
            now: DateTimeOffset.UtcNow);

        Assert.True(result.Success);
        Assert.Equal(new[] { recent }, result.RetainedFiles);
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unknown));
    }

    [Fact]
    public void Prune_RejectsInvalidPolicyWithoutDeletingBackups()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var backup = CreateBackup(temp.Path, "package.json", 1, -8);

        var result = ProfileBackupRetention.Prune(
            temp.Path,
            ["../package.json"],
            maximumRetainedPerManifest: 1);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.True(File.Exists(backup));
    }

    private static string CreateBackup(string root, string manifest, int sequence, int minutesAgo)
    {
        var path = Path.Combine(root, manifest + ".bak-20260919-00000" + sequence);
        File.WriteAllText(path, "backup " + sequence);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutesAgo));
        return path;
    }
}
