using System.IO.Compression;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class PortableReleaseArchiveTests
{
    [Fact]
    public void Validate_AcceptsVerifiedPortableReleaseWithoutWritingAnArchive()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var publish = CreateVerifiedPublishRoot(temp.Path);

        var result = PortableReleaseArchive.Validate(publish);

        Assert.True(result.Success);
        Assert.Equal(PortableReleaseArchiveStatus.Validated, result.Status);
        Assert.Equal(8, result.FileCount);
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public void Create_ArchivesVerifiedPortableReleaseDeterministically()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var publish = CreateVerifiedPublishRoot(temp.Path);
        var first = Path.Combine(temp.Path, "first.zip");
        var second = Path.Combine(temp.Path, "second.zip");

        var firstResult = PortableReleaseArchive.Create(publish, first);
        var secondResult = PortableReleaseArchive.Create(publish, second);

        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using var archive = ZipFile.OpenRead(first);
        Assert.Equal(new[] { "DSHDesktop.dll", "DSHDesktop.exe", "runtime/active.json" },
            archive.Entries.Select(entry => entry.FullName).Where(name => !name.Contains("versions/", StringComparison.Ordinal)).ToArray());
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("desktop-runtime.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_RejectsShellOnlyPublishWithoutVerifiedRuntime()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var publish = Path.Combine(temp.Path, "publish");
        Directory.CreateDirectory(publish);
        File.WriteAllText(Path.Combine(publish, "DSHDesktop.exe"), "exe");
        File.WriteAllText(Path.Combine(publish, "DSHDesktop.dll"), "dll");

        var result = PortableReleaseArchive.Create(publish, Path.Combine(temp.Path, "portable.zip"));

        Assert.Equal(PortableReleaseArchiveStatus.RuntimeNotReady, result.Status);
        Assert.False(File.Exists(Path.Combine(temp.Path, "portable.zip")));
    }

    [Fact]
    public void Create_RejectsOutputWithinPublishRoot()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var publish = CreateVerifiedPublishRoot(temp.Path);

        var result = PortableReleaseArchive.Create(publish, Path.Combine(publish, "portable.zip"));

        Assert.Equal(PortableReleaseArchiveStatus.InvalidRequest, result.Status);
    }

    private static string CreateVerifiedPublishRoot(string root)
    {
        var publish = Path.Combine(root, "publish");
        Directory.CreateDirectory(publish);
        File.WriteAllText(Path.Combine(publish, "DSHDesktop.exe"), "exe");
        File.WriteAllText(Path.Combine(publish, "DSHDesktop.dll"), "dll");
        var runtimeRoot = Path.Combine(publish, "runtime");
        RuntimeSlotManagerTests.CreateSlot(runtimeRoot, "runtime-a", "runtime");
        Assert.True(new RuntimeSlotManager(runtimeRoot).Activate("runtime-a").Success);
        return publish;
    }
}
