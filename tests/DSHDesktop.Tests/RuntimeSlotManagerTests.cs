using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeSlotManagerTests
{
    [Fact]
    public void ResolveActive_ReportsMissingSelection()
    {
        using var temp = new TempDirectory();

        var result = new RuntimeSlotManager(temp.Path).ResolveActive();

        Assert.Equal(RuntimeSlotStatus.MissingSelection, result.Status);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void Activate_VerifiesCandidateAndWritesAtomicSelection()
    {
        using var temp = new TempDirectory();
        CreateSlot(temp.Path, "runtime-a", "a");
        var manager = new RuntimeSlotManager(temp.Path);

        var activated = manager.Activate("runtime-a");
        var resolved = manager.ResolveActive();

        Assert.True(activated.Success);
        Assert.True(resolved.IsReady);
        Assert.Equal("runtime-a", resolved.Selection?.ActiveRuntimeId);
        Assert.Equal("runtime-a", resolved.Manifest?.RuntimeId);
        Assert.True(File.Exists(manager.SelectionPath));
    }

    [Fact]
    public void DamagedCandidate_DoesNotReplaceCurrentSelection()
    {
        using var temp = new TempDirectory();
        var manager = new RuntimeSlotManager(temp.Path);
        CreateSlot(temp.Path, "runtime-a", "a");
        Assert.True(manager.Activate("runtime-a").Success);
        var candidate = CreateSlot(temp.Path, "runtime-b", "before");
        File.WriteAllText(Path.Combine(candidate, "node", "node.exe"), "tampered-and-longer");

        var rejected = manager.Activate("runtime-b");
        var active = manager.ResolveActive(verifyFiles: false);

        Assert.False(rejected.Success);
        Assert.Equal("runtime-a", active.Selection?.ActiveRuntimeId);
    }

    [Fact]
    public void Rollback_SwitchesToPreviouslyVerifiedSlot()
    {
        using var temp = new TempDirectory();
        var manager = new RuntimeSlotManager(temp.Path);
        CreateSlot(temp.Path, "runtime-a", "a");
        CreateSlot(temp.Path, "runtime-b", "b");
        manager.Activate("runtime-a");
        manager.Activate("runtime-b");

        var rollback = manager.Rollback();

        Assert.True(rollback.Success);
        Assert.Equal("runtime-a", manager.ResolveActive().Selection?.ActiveRuntimeId);
        Assert.Equal("runtime-b", manager.ResolveActive().Selection?.PreviousRuntimeId);
    }

    [Fact]
    public void PathLikeRuntimeId_IsRejected()
    {
        using var temp = new TempDirectory();
        var manager = new RuntimeSlotManager(temp.Path);

        var result = manager.Resolve("../outside");

        Assert.Equal(RuntimeSlotStatus.InvalidRuntimeId, result.Status);
        Assert.False(manager.Activate("../outside").Success);
    }

    [Fact]
    public void QuarantineInactive_MovesRejectedCandidateAndClearsPreviousPointer()
    {
        using var temp = new TempDirectory();
        var manager = new RuntimeSlotManager(temp.Path);
        CreateSlot(temp.Path, "runtime-a", "a");
        CreateSlot(temp.Path, "runtime-b", "b");
        Assert.True(manager.Activate("runtime-a").Success);
        Assert.True(manager.Activate("runtime-b").Success);
        Assert.True(manager.Rollback().Success);

        var quarantined = manager.QuarantineInactive("runtime-b");
        var active = manager.ResolveActive();

        Assert.True(quarantined.Success);
        Assert.NotNull(quarantined.QuarantineDirectory);
        Assert.True(Directory.Exists(quarantined.QuarantineDirectory));
        Assert.False(Directory.Exists(Path.Combine(
            temp.Path,
            RuntimeSlotManager.VersionsDirectoryName,
            "runtime-b")));
        Assert.Equal("runtime-a", active.Selection?.ActiveRuntimeId);
        Assert.Null(active.Selection?.PreviousRuntimeId);
        Assert.False(manager.QuarantineInactive("runtime-a").Success);
    }

    [Fact]
    public void PruneInactiveAndStaging_RetainsActiveAndRollbackSlots()
    {
        using var temp = new TempDirectory();
        var manager = new RuntimeSlotManager(temp.Path);
        CreateSlot(temp.Path, "runtime-a", "a");
        CreateSlot(temp.Path, "runtime-b", "b");
        CreateSlot(temp.Path, "runtime-c", "c");
        Assert.True(manager.Activate("runtime-a").Success);
        Assert.True(manager.Activate("runtime-b").Success);

        var stagingRoot = Path.Combine(temp.Path, RuntimeSlotManager.StagingDirectoryName);
        var stale = Path.Combine(stagingRoot, "stale-install");
        var recent = Path.Combine(stagingRoot, "recent-install");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(recent);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        var result = manager.PruneInactiveAndStaging(
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow);

        Assert.True(result.Success);
        Assert.Equal(new[] { "runtime-b", "runtime-a" }, result.RetainedRuntimeIds);
        Assert.Equal(new[] { "runtime-c" }, result.RemovedRuntimeIds);
        Assert.False(Directory.Exists(Path.Combine(
            temp.Path, RuntimeSlotManager.VersionsDirectoryName, "runtime-c")));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.Equal("runtime-b", manager.ResolveActive().Selection?.ActiveRuntimeId);
        Assert.Equal("runtime-a", manager.ResolveActive().Selection?.PreviousRuntimeId);
    }

    [Fact]
    public void PruneInactiveAndStaging_KeepsDamagedSlotsForManualRecovery()
    {
        using var temp = new TempDirectory();
        var manager = new RuntimeSlotManager(temp.Path);
        CreateSlot(temp.Path, "runtime-a", "a");
        var damaged = CreateSlot(temp.Path, "runtime-b", "b");
        File.WriteAllText(Path.Combine(damaged, "node", "node.exe"), "tampered");
        Assert.True(manager.Activate("runtime-a").Success);

        var result = manager.PruneInactiveAndStaging();

        Assert.True(result.Success);
        Assert.Empty(result.RemovedRuntimeIds);
        Assert.Contains(damaged, result.SkippedDirectories);
        Assert.True(Directory.Exists(damaged));
    }

    internal static string CreateSlot(string runtimeRoot, string runtimeId, string content)
    {
        var slot = Path.Combine(runtimeRoot, RuntimeSlotManager.VersionsDirectoryName, runtimeId);
        Write(slot, "node/node.exe", content);
        Write(slot, "dsh/node_modules/@deepseek-ai/dsh/lib/bin.js", "entry-" + content);
        Write(slot, "dsh/node_modules/@deepseek-ai/dsh/package.json", "{\"version\":\"0.1.5-alpha.1\"}");
        Write(slot, "pnpm/node_modules/pnpm/package.json", "{\"version\":\"11.27.0\"}");
        var manifest = RuntimeManifest.Create(slot, new RuntimeManifestIdentity(
            runtimeId,
            "1.0.0",
            "0.1.5-alpha.1",
            "24.20.0",
            "11.27.0",
            "win32",
            "x64",
            1));
        manifest.Write(Path.Combine(slot, RuntimeManifest.FileName));
        return slot;
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dsh-runtime-slot-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
