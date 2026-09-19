using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeUpdateStagerTests
{
    [Fact]
    public void AdoptCandidate_MovesVerifiedStagedRuntimeAndPreservesRollbackSlot()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        RuntimeSlotManagerTests.CreateSlot(writableRoot, "runtime-a", "a");
        Assert.True(new RuntimeSlotManager(writableRoot).Activate("runtime-a").Success);
        var candidate = CreateStagedCandidate(temp.Path, writableRoot, "runtime-b", "b");

        var result = new RuntimeUpdateStager(writableRoot).AdoptAndActivateCandidate(candidate);
        var active = new RuntimeSlotManager(writableRoot).ResolveActive();

        Assert.Equal(RuntimeUpdateStageStatus.Activated, result.Status);
        Assert.Equal("runtime-b", active.Selection?.ActiveRuntimeId);
        Assert.Equal("runtime-a", active.Selection?.PreviousRuntimeId);
        Assert.False(Directory.Exists(candidate));
        Assert.True(Directory.Exists(Path.Combine(
            writableRoot, RuntimeSlotManager.VersionsDirectoryName, "runtime-b")));
    }

    [Fact]
    public void AdoptCandidate_RejectsDirectoryOutsideItsStagingRoot()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var outside = RuntimeSlotManagerTests.CreateSlot(Path.Combine(temp.Path, "outside"), "runtime-b", "b");

        var result = new RuntimeUpdateStager(writableRoot).AdoptAndActivateCandidate(outside);

        Assert.Equal(RuntimeUpdateStageStatus.CandidateOutsideStaging, result.Status);
        Assert.True(Directory.Exists(outside));
        Assert.False(Directory.Exists(Path.Combine(writableRoot, RuntimeSlotManager.VersionsDirectoryName)));
    }

    [Fact]
    public void AdoptCandidate_LeavesDamagedStagedDirectoryForDiagnosis()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var candidate = CreateStagedCandidate(temp.Path, writableRoot, "runtime-b", "b");
        File.WriteAllText(Path.Combine(candidate, "node", "node.exe"), "tampered");

        var result = new RuntimeUpdateStager(writableRoot).AdoptAndActivateCandidate(candidate);

        Assert.Equal(RuntimeUpdateStageStatus.CandidateInvalid, result.Status);
        Assert.True(Directory.Exists(candidate));
        Assert.False(Directory.Exists(Path.Combine(
            writableRoot, RuntimeSlotManager.VersionsDirectoryName, "runtime-b")));
    }

    [Fact]
    public void AdoptCandidate_ReusesOnlyEquivalentExistingSlot()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        RuntimeSlotManagerTests.CreateSlot(writableRoot, "runtime-a", "a");
        RuntimeSlotManagerTests.CreateSlot(writableRoot, "runtime-b", "b");
        Assert.True(new RuntimeSlotManager(writableRoot).Activate("runtime-a").Success);
        var candidate = CreateStagedCandidate(temp.Path, writableRoot, "runtime-b", "b");

        var result = new RuntimeUpdateStager(writableRoot).AdoptAndActivateCandidate(candidate);

        Assert.Equal(RuntimeUpdateStageStatus.ActivatedExisting, result.Status);
        Assert.False(Directory.Exists(candidate));
        Assert.Equal("runtime-b", new RuntimeSlotManager(writableRoot)
            .ResolveActive().Selection?.ActiveRuntimeId);
    }

    [Fact]
    public async Task StageAndActivate_CopiesBaselineBuildsCandidateAndSwitchesAtomically()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        var sourceManifest = Path.Combine(sourceSlot, RuntimeManifest.FileName);
        var stager = new RuntimeUpdateStager(writableRoot);

        var result = await stager.StageAndActivateAsync(
            sourceSlot,
            sourceManifest,
            "0.1.6-alpha.1",
            (context, _) =>
            {
                WriteVersion(context.DshInstallDirectory, "0.1.6-alpha.1");
                File.WriteAllText(
                    Path.Combine(context.DshInstallDirectory, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
                    "updated-entry");
                return Task.FromResult(RuntimeUpdateInstallResult.Ok());
            });

        Assert.True(result.Success);
        Assert.Equal(RuntimeUpdateStageStatus.Activated, result.Status);
        Assert.Equal("entry-original", File.ReadAllText(Path.Combine(
            sourceSlot, "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")));
        var slots = new RuntimeSlotManager(writableRoot);
        var active = slots.ResolveActive();
        Assert.True(active.IsReady);
        Assert.Equal(result.RuntimeId, active.Selection?.ActiveRuntimeId);
        Assert.Equal("runtime-a", active.Selection?.PreviousRuntimeId);
        Assert.Equal("0.1.6-alpha.1", active.Manifest?.DshVersion);
        Assert.Equal("updated-entry", File.ReadAllText(Path.Combine(
            active.SlotDirectory!, "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")));
    }

    [Fact]
    public async Task StageAndActivate_InstallerFailureKeepsVerifiedBaselineActive()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        var stager = new RuntimeUpdateStager(writableRoot);

        var result = await stager.StageAndActivateAsync(
            sourceSlot,
            Path.Combine(sourceSlot, RuntimeManifest.FileName),
            "0.1.6-alpha.1",
            (_, _) => Task.FromResult(RuntimeUpdateInstallResult.Fail("registry unavailable")));

        Assert.False(result.Success);
        Assert.Equal(RuntimeUpdateStageStatus.InstallFailed, result.Status);
        var active = new RuntimeSlotManager(writableRoot).ResolveActive();
        Assert.True(active.IsReady);
        Assert.Equal("runtime-a", active.Selection?.ActiveRuntimeId);
        Assert.Null(active.Selection?.PreviousRuntimeId);
        Assert.Empty(Directory.GetDirectories(Path.Combine(writableRoot, "staging")));
    }

    [Fact]
    public async Task StageAndActivate_VersionMismatchNeverPublishesCandidate()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        var stager = new RuntimeUpdateStager(writableRoot);

        var result = await stager.StageAndActivateAsync(
            sourceSlot,
            Path.Combine(sourceSlot, RuntimeManifest.FileName),
            "0.1.6-alpha.1",
            (context, _) =>
            {
                WriteVersion(context.DshInstallDirectory, "9.9.9");
                return Task.FromResult(RuntimeUpdateInstallResult.Ok());
            });

        Assert.Equal(RuntimeUpdateStageStatus.VersionMismatch, result.Status);
        Assert.Equal("runtime-a", new RuntimeSlotManager(writableRoot)
            .ResolveActive().Selection?.ActiveRuntimeId);
        Assert.Single(Directory.GetDirectories(Path.Combine(writableRoot, "versions")));
    }

    [Fact]
    public async Task StageAndActivate_DamagedSourceDoesNotCreateWritableRuntime()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        File.WriteAllText(Path.Combine(sourceSlot, "node", "node.exe"), "tampered");
        var installerCalled = false;

        var result = await new RuntimeUpdateStager(writableRoot).StageAndActivateAsync(
            sourceSlot,
            Path.Combine(sourceSlot, RuntimeManifest.FileName),
            "0.1.6-alpha.1",
            (_, _) =>
            {
                installerCalled = true;
                return Task.FromResult(RuntimeUpdateInstallResult.Ok());
            });

        Assert.Equal(RuntimeUpdateStageStatus.SourceInvalid, result.Status);
        Assert.False(installerCalled);
        Assert.False(Directory.Exists(writableRoot));
    }

    [Fact]
    public async Task StageAndActivate_ReusesOnlyAnAlreadyVerifiedCandidate()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        var sourceManifest = Path.Combine(sourceSlot, RuntimeManifest.FileName);
        var stager = new RuntimeUpdateStager(writableRoot);
        var installerCalls = 0;
        Task<RuntimeUpdateInstallResult> Install(RuntimeUpdateInstallContext context, CancellationToken _)
        {
            installerCalls++;
            WriteVersion(context.DshInstallDirectory, "0.1.6-alpha.1");
            return Task.FromResult(RuntimeUpdateInstallResult.Ok());
        }

        var first = await stager.StageAndActivateAsync(
            sourceSlot, sourceManifest, "0.1.6-alpha.1", Install);
        var second = await stager.StageAndActivateAsync(
            sourceSlot, sourceManifest, "0.1.6-alpha.1", Install);

        Assert.Equal(RuntimeUpdateStageStatus.Activated, first.Status);
        Assert.Equal(RuntimeUpdateStageStatus.ActivatedExisting, second.Status);
        Assert.Equal(1, installerCalls);
    }

    [Fact]
    public async Task StageAndActivate_PrunesVerifiedSlotsBeyondActiveAndRollback()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        RuntimeSlotManagerTests.CreateSlot(writableRoot, "runtime-old", "old");

        var result = await new RuntimeUpdateStager(writableRoot).StageAndActivateAsync(
            sourceSlot,
            Path.Combine(sourceSlot, RuntimeManifest.FileName),
            "0.1.6-alpha.1",
            (context, _) =>
            {
                WriteVersion(context.DshInstallDirectory, "0.1.6-alpha.1");
                return Task.FromResult(RuntimeUpdateInstallResult.Ok());
            });

        Assert.Equal(RuntimeUpdateStageStatus.Activated, result.Status);
        Assert.Null(result.MaintenanceWarning);
        Assert.False(Directory.Exists(Path.Combine(
            writableRoot, RuntimeSlotManager.VersionsDirectoryName, "runtime-old")));
        Assert.True(Directory.Exists(Path.Combine(
            writableRoot, RuntimeSlotManager.VersionsDirectoryName, "runtime-a")));
        Assert.True(Directory.Exists(result.SlotDirectory));
    }

    [Fact]
    public async Task StageAndActivate_PrunesRejectedEvidenceBeyondDefaultRetention()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "original");
        var rejectedRoot = Path.Combine(writableRoot, "rejected");
        var rejected = Enumerable.Range(0, 4).Select(index =>
        {
            var directory = Path.Combine(rejectedRoot,
                "runtime-failed-2026091900000000" + index + "-abcdef1" + index);
            Directory.CreateDirectory(directory);
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddDays(-8 - index));
            return directory;
        }).ToArray();

        var result = await new RuntimeUpdateStager(writableRoot).StageAndActivateAsync(
            sourceSlot,
            Path.Combine(sourceSlot, RuntimeManifest.FileName),
            "0.1.6-alpha.1",
            (context, _) =>
            {
                WriteVersion(context.DshInstallDirectory, "0.1.6-alpha.1");
                return Task.FromResult(RuntimeUpdateInstallResult.Ok());
            });

        Assert.True(result.Success);
        Assert.Null(result.MaintenanceWarning);
        Assert.Equal(3, rejected.Count(Directory.Exists));
        Assert.False(Directory.Exists(rejected[3]));
    }

    [Fact]
    public async Task StageAndActivate_RejectsDifferentBuildWithSameBaselineRuntimeId()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var sourceRoot = Path.Combine(temp.Path, "installed-runtime");
        var writableRoot = Path.Combine(temp.Path, "writable-runtime");
        var sourceSlot = RuntimeSlotManagerTests.CreateSlot(sourceRoot, "runtime-a", "installed-build");
        RuntimeSlotManagerTests.CreateSlot(writableRoot, "runtime-a", "different-build");
        var installerCalled = false;

        var result = await new RuntimeUpdateStager(writableRoot).StageAndActivateAsync(
            sourceSlot,
            Path.Combine(sourceSlot, RuntimeManifest.FileName),
            "0.1.6-alpha.1",
            (_, _) =>
            {
                installerCalled = true;
                return Task.FromResult(RuntimeUpdateInstallResult.Ok());
            });

        Assert.Equal(RuntimeUpdateStageStatus.CandidateConflict, result.Status);
        Assert.False(installerCalled);
        Assert.Equal(RuntimeSlotStatus.MissingSelection, new RuntimeSlotManager(writableRoot).ResolveActive().Status);
    }

    private static void WriteVersion(string dshInstallDirectory, string version)
    {
        var manifest = Path.Combine(
            dshInstallDirectory,
            "node_modules", "@deepseek-ai", "dsh", "package.json");
        File.WriteAllText(manifest, "{\"version\":\"" + version + "\"}");
    }

    private static string CreateStagedCandidate(
        string root,
        string writableRoot,
        string runtimeId,
        string content)
    {
        var sourceRoot = Path.Combine(root, "candidate-source-" + runtimeId + "-" + content);
        var source = RuntimeSlotManagerTests.CreateSlot(sourceRoot, runtimeId, content);
        var staging = Path.Combine(writableRoot, RuntimeSlotManager.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        var candidate = Path.Combine(staging, ".runtime-" + runtimeId + "-" + content);
        Directory.Move(source, candidate);
        return candidate;
    }
}
