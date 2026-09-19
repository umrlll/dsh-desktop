using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public enum RuntimeUpdateStageStatus
{
    Activated,
    ActivatedExisting,
    InvalidRequest,
    SourceInvalid,
    CandidateConflict,
    InstallFailed,
    VersionMismatch,
    ManifestFailed,
    ActivationFailed,
    Cancelled,
}

public sealed record RuntimeUpdateInstallContext(
    string RuntimeId,
    string StagingDirectory,
    string DshInstallDirectory,
    string NodePath,
    string TargetDshVersion);

public sealed record RuntimeUpdateInstallResult(bool Success, string? Error = null)
{
    public static RuntimeUpdateInstallResult Ok() => new(true);
    public static RuntimeUpdateInstallResult Fail(string error) => new(false, error);
}

public sealed record RuntimeUpdateStageResult(
    RuntimeUpdateStageStatus Status,
    string? RuntimeId = null,
    string? SlotDirectory = null,
    string? Error = null,
    string? MaintenanceWarning = null)
{
    public bool Success => Status is RuntimeUpdateStageStatus.Activated
        or RuntimeUpdateStageStatus.ActivatedExisting;
}

/// <summary>
/// Builds runtime updates outside the active slot. The current verified slot is first materialized
/// in the writable runtime root, then copied to staging, mutated by the supplied installer,
/// re-manifested, verified, moved into versions, and finally activated atomically.
/// </summary>
public sealed class RuntimeUpdateStager
{
    private static readonly Regex ExactVersion = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _runtimeRoot;

    public RuntimeUpdateStager(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
    }

    public async Task<RuntimeUpdateStageResult> StageAndActivateAsync(
        string sourceSlotDirectory,
        string sourceManifestPath,
        string targetDshVersion,
        Func<RuntimeUpdateInstallContext, CancellationToken, Task<RuntimeUpdateInstallResult>> installer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installer);
        if (!ExactVersion.IsMatch(targetDshVersion))
            return new RuntimeUpdateStageResult(
                RuntimeUpdateStageStatus.InvalidRequest,
                Error: "目标 DSH 版本必须是精确语义版本。");

        RuntimeManifest sourceManifest;
        try
        {
            sourceSlotDirectory = Path.GetFullPath(sourceSlotDirectory);
            sourceManifestPath = Path.GetFullPath(sourceManifestPath);
            sourceManifest = RuntimeManifest.Load(sourceManifestPath);
            var sourceIssues = sourceManifest.Verify(sourceSlotDirectory);
            if (sourceIssues.Count > 0)
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.SourceInvalid,
                    sourceManifest.RuntimeId,
                    sourceSlotDirectory,
                    DescribeIssues(sourceIssues));
            if (!RuntimeSlotManager.IsSafeRuntimeId(sourceManifest.RuntimeId))
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.SourceInvalid,
                    sourceManifest.RuntimeId,
                    sourceSlotDirectory,
                    "源 manifest 的 runtimeId 不安全。");
        }
        catch (Exception ex)
        {
            return new RuntimeUpdateStageResult(RuntimeUpdateStageStatus.SourceInvalid, Error: ex.Message);
        }

        var runtimeId = BuildRuntimeId(sourceManifest, targetDshVersion);
        if (!RuntimeSlotManager.IsSafeRuntimeId(runtimeId))
            return new RuntimeUpdateStageResult(
                RuntimeUpdateStageStatus.InvalidRequest,
                runtimeId,
                Error: "生成的候选 runtimeId 不安全或过长。");

        var versionsRoot = Path.Combine(_runtimeRoot, RuntimeSlotManager.VersionsDirectoryName);
        var stagingRoot = Path.Combine(_runtimeRoot, "staging");
        var baseSlot = Path.Combine(versionsRoot, sourceManifest.RuntimeId);
        var candidateSlot = Path.Combine(versionsRoot, runtimeId);
        string? staging = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(versionsRoot);
            Directory.CreateDirectory(stagingRoot);

            var baseResult = EnsureBaseSlot(
                sourceSlotDirectory,
                sourceManifest,
                baseSlot,
                stagingRoot,
                cancellationToken);
            if (baseResult != null) return baseResult;

            var slots = new RuntimeSlotManager(_runtimeRoot);
            var active = slots.ResolveActive(verifyFiles: true);
            if (!active.IsReady
                || !string.Equals(active.Selection?.ActiveRuntimeId, sourceManifest.RuntimeId, StringComparison.Ordinal))
            {
                var baseActivation = slots.Activate(sourceManifest.RuntimeId);
                if (!baseActivation.Success)
                    return new RuntimeUpdateStageResult(
                        RuntimeUpdateStageStatus.ActivationFailed,
                        sourceManifest.RuntimeId,
                        baseSlot,
                        "无法建立可回退基线槽：" + baseActivation.Error);
            }

            if (Directory.Exists(candidateSlot))
            {
                var existing = slots.Resolve(runtimeId, verifyFiles: true);
                if (!existing.IsReady
                    || !string.Equals(existing.Manifest?.DshVersion, targetDshVersion, StringComparison.Ordinal))
                {
                    return new RuntimeUpdateStageResult(
                        RuntimeUpdateStageStatus.CandidateConflict,
                        runtimeId,
                        candidateSlot,
                        "同名候选槽已存在但未通过完整性或版本校验；未覆盖该目录。");
                }

                var existingActivation = slots.Activate(runtimeId);
                if (!existingActivation.Success)
                    return new RuntimeUpdateStageResult(
                        RuntimeUpdateStageStatus.ActivationFailed,
                        runtimeId,
                        candidateSlot,
                        existingActivation.Error);
                var existingMaintenanceWarning = DescribeMaintenance(slots.PruneInactiveAndStaging());
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.ActivatedExisting,
                    runtimeId,
                    candidateSlot,
                    MaintenanceWarning: existingMaintenanceWarning);
            }

            staging = Path.Combine(stagingRoot, runtimeId + "-" + Guid.NewGuid().ToString("N"));
            CopyManifestFiles(baseSlot, sourceManifest, staging, cancellationToken);
            var dshInstall = Path.Combine(staging, "dsh");
            var nodePath = Path.Combine(staging, "node", OperatingSystem.IsWindows() ? "node.exe" : "node");
            var installResult = await installer(
                new RuntimeUpdateInstallContext(runtimeId, staging, dshInstall, nodePath, targetDshVersion),
                cancellationToken).ConfigureAwait(false);
            if (!installResult.Success)
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.InstallFailed,
                    runtimeId,
                    Error: installResult.Error ?? "DSH 安装器返回失败。");

            var installedVersion = ReadPackageVersion(Path.Combine(
                dshInstall,
                "node_modules", "@deepseek-ai", "dsh", "package.json"));
            if (!string.Equals(installedVersion, targetDshVersion, StringComparison.Ordinal))
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.VersionMismatch,
                    runtimeId,
                    Error: $"候选 DSH 版本不匹配：期望 {targetDshVersion}，实际 {installedVersion ?? "不可用"}。");

            var identity = new RuntimeManifestIdentity(
                runtimeId,
                sourceManifest.DesktopVersion,
                targetDshVersion,
                sourceManifest.NodeVersion,
                sourceManifest.PnpmVersion,
                sourceManifest.Platform,
                sourceManifest.Architecture,
                sourceManifest.ProfileSchema);
            var candidateManifest = RuntimeManifest.Create(staging, identity);
            var candidateManifestPath = Path.Combine(staging, RuntimeManifest.FileName);
            candidateManifest.Write(candidateManifestPath);
            var issues = RuntimeManifest.Load(candidateManifestPath).Verify(staging);
            if (issues.Count > 0)
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.ManifestFailed,
                    runtimeId,
                    Error: DescribeIssues(issues));

            Directory.Move(staging, candidateSlot);
            staging = null;
            var activation = slots.Activate(runtimeId);
            if (!activation.Success)
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.ActivationFailed,
                    runtimeId,
                    candidateSlot,
                    activation.Error);
            var activatedMaintenanceWarning = DescribeMaintenance(slots.PruneInactiveAndStaging());
            return new RuntimeUpdateStageResult(
                RuntimeUpdateStageStatus.Activated,
                runtimeId,
                candidateSlot,
                MaintenanceWarning: activatedMaintenanceWarning);
        }
        catch (OperationCanceledException)
        {
            return new RuntimeUpdateStageResult(RuntimeUpdateStageStatus.Cancelled, runtimeId);
        }
        catch (Exception ex)
        {
            return new RuntimeUpdateStageResult(
                RuntimeUpdateStageStatus.ManifestFailed,
                runtimeId,
                Error: ex.Message);
        }
        finally
        {
            if (staging != null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch { /* staging cleanup is best effort */ }
            }
        }
    }

    private static RuntimeUpdateStageResult? EnsureBaseSlot(
        string sourceSlot,
        RuntimeManifest manifest,
        string baseSlot,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(baseSlot))
        {
            var existing = new RuntimeSlotManager(Path.GetDirectoryName(Path.GetDirectoryName(baseSlot)!)!)
                .Resolve(manifest.RuntimeId, verifyFiles: true);
            return existing.IsReady && ManifestsEquivalent(manifest, existing.Manifest)
                ? null
                : new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.CandidateConflict,
                    manifest.RuntimeId,
                    baseSlot,
                    "可写运行时根中的基线槽与源 manifest 不一致；未覆盖该目录。");
        }

        var staging = Path.Combine(stagingRoot, ".base-" + manifest.RuntimeId + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyManifestFiles(sourceSlot, manifest, staging, cancellationToken);
            manifest.Write(Path.Combine(staging, RuntimeManifest.FileName));
            var issues = manifest.Verify(staging);
            if (issues.Count > 0)
                return new RuntimeUpdateStageResult(
                    RuntimeUpdateStageStatus.SourceInvalid,
                    manifest.RuntimeId,
                    Error: "复制后的基线槽校验失败：" + DescribeIssues(issues));
            Directory.Move(staging, baseSlot);
            staging = string.Empty;
            return null;
        }
        finally
        {
            try { if (staging.Length > 0 && Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* staging cleanup is best effort */ }
        }
    }

    private static bool ManifestsEquivalent(RuntimeManifest expected, RuntimeManifest? actual)
    {
        if (actual == null
            || expected.SchemaVersion != actual.SchemaVersion
            || !string.Equals(expected.RuntimeId, actual.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(expected.DesktopVersion, actual.DesktopVersion, StringComparison.Ordinal)
            || !string.Equals(expected.DshVersion, actual.DshVersion, StringComparison.Ordinal)
            || !string.Equals(expected.NodeVersion, actual.NodeVersion, StringComparison.Ordinal)
            || !string.Equals(expected.PnpmVersion, actual.PnpmVersion, StringComparison.Ordinal)
            || !string.Equals(expected.Platform, actual.Platform, StringComparison.Ordinal)
            || !string.Equals(expected.Architecture, actual.Architecture, StringComparison.Ordinal)
            || expected.ProfileSchema != actual.ProfileSchema
            || expected.Files.Count != actual.Files.Count)
            return false;

        for (var i = 0; i < expected.Files.Count; i++)
        {
            if (!Equals(expected.Files[i], actual.Files[i])) return false;
        }
        return true;
    }

    private static void CopyManifestFiles(
        string sourceRoot,
        RuntimeManifest manifest,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(sourceRoot, relative);
            var destination = Path.Combine(destinationRoot, relative);
            var parent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException("候选文件路径缺少父目录。");
            Directory.CreateDirectory(parent);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static string BuildRuntimeId(RuntimeManifest source, string dshVersion)
        => "dsh-" + SafeToken(dshVersion)
            + "-" + SafeToken(source.Platform)
            + "-" + SafeToken(source.Architecture)
            + "-node-" + SafeToken(source.NodeVersion)
            + "-pnpm-" + SafeToken(source.PnpmVersion);

    private static string SafeToken(string value)
        => Regex.Replace(value, @"[^0-9A-Za-z._-]", "-");

    private static string? ReadPackageVersion(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeIssues(IEnumerable<RuntimeManifestIssue> issues)
        => string.Join("；", issues.Take(5).Select(issue =>
            issue.Code + (issue.Path == null ? string.Empty : "(" + issue.Path + ")")));

    private static string? DescribeMaintenance(RuntimeStoreMaintenanceResult result)
        => result.Success
            ? null
            : "运行时槽清理未完成（已保留活动槽）："
                + string.Join("；", result.Errors.Take(3));
}
