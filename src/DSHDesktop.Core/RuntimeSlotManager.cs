using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record RuntimeSlotSelection(
    int SchemaVersion,
    string ActiveRuntimeId,
    string? PreviousRuntimeId = null);

public enum RuntimeSlotStatus
{
    Ready,
    MissingSelection,
    InvalidSelection,
    InvalidRuntimeId,
    MissingSlot,
    MissingManifest,
    InvalidManifest,
    IntegrityFailed,
}

public sealed record RuntimeSlotResolution(
    RuntimeSlotStatus Status,
    string? SlotDirectory = null,
    string? ManifestPath = null,
    RuntimeManifest? Manifest = null,
    RuntimeSlotSelection? Selection = null,
    IReadOnlyList<RuntimeManifestIssue>? Issues = null,
    string? Error = null)
{
    public bool IsReady => Status == RuntimeSlotStatus.Ready;
}

public sealed record RuntimeSlotActivationResult(
    bool Success,
    RuntimeSlotSelection? Selection = null,
    string? Error = null);

public sealed record RuntimeSlotQuarantineResult(
    bool Success,
    string? QuarantineDirectory = null,
    string? Error = null);

/// <summary>
/// Records a conservative runtime-store cleanup. Only slots whose manifest fully verifies may be
/// deleted; unknown, damaged, recent staging, and reparse-point directories are retained for
/// manual recovery and evidence collection.
/// </summary>
public sealed record RuntimeStoreMaintenanceResult(
    bool Success,
    IReadOnlyList<string> RetainedRuntimeIds,
    IReadOnlyList<string> RemovedRuntimeIds,
    IReadOnlyList<string> RemovedStagingDirectories,
    IReadOnlyList<string> SkippedDirectories,
    IReadOnlyList<string> Errors);

/// <summary>
/// Records conservative cleanup of rejected runtime candidates. Entries kept for the minimum
/// evidence period are never removed merely because the count limit has been reached.
/// </summary>
public sealed record RuntimeRejectedMaintenanceResult(
    bool Success,
    IReadOnlyList<string> RetainedDirectories,
    IReadOnlyList<string> RemovedDirectories,
    IReadOnlyList<string> SkippedDirectories,
    IReadOnlyList<string> Errors);

/// <summary>
/// Resolves and atomically switches immutable runtime slots under runtime/versions. A pointer is
/// never updated until the candidate manifest and every declared file have been verified.
/// </summary>
public sealed class RuntimeSlotManager
{
    public const string SelectionFileName = "active.json";
    public const string VersionsDirectoryName = "versions";
    public const string StagingDirectoryName = "staging";

    private static readonly Regex SafeRuntimeId = new(
        @"^[0-9A-Za-z][0-9A-Za-z._-]{0,199}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SafeRejectedDirectoryName = new(
        @"^[0-9A-Za-z][0-9A-Za-z._-]{0,199}-\d{17}-[0-9a-f]{8}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _runtimeRoot;

    public RuntimeSlotManager(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
    }

    public string SelectionPath => Path.Combine(_runtimeRoot, SelectionFileName);

    public RuntimeSlotResolution ResolveActive(bool verifyFiles = true)
    {
        RuntimeSlotSelection? selection;
        try
        {
            if (!File.Exists(SelectionPath))
                return new RuntimeSlotResolution(RuntimeSlotStatus.MissingSelection);
            selection = JsonSerializer.Deserialize<RuntimeSlotSelection>(
                File.ReadAllText(SelectionPath),
                JsonOptions());
            if (selection == null || selection.SchemaVersion != 1
                || string.IsNullOrWhiteSpace(selection.ActiveRuntimeId))
            {
                return new RuntimeSlotResolution(
                    RuntimeSlotStatus.InvalidSelection,
                    Error: "active.json 缺少有效的 schemaVersion/activeRuntimeId。");
            }
        }
        catch (Exception ex)
        {
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.InvalidSelection,
                Error: ex.Message);
        }

        return Resolve(selection.ActiveRuntimeId, selection, verifyFiles);
    }

    public RuntimeSlotResolution Resolve(
        string runtimeId,
        RuntimeSlotSelection? selection = null,
        bool verifyFiles = true)
    {
        if (!IsSafeRuntimeId(runtimeId))
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.InvalidRuntimeId,
                Selection: selection,
                Error: "runtimeId 含不允许的路径字符。");

        var slot = Path.Combine(_runtimeRoot, VersionsDirectoryName, runtimeId);
        if (!Directory.Exists(slot))
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.MissingSlot,
                slot,
                Selection: selection,
                Error: "活动运行时槽不存在。");

        var manifestPath = Path.Combine(slot, RuntimeManifest.FileName);
        if (!File.Exists(manifestPath))
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.MissingManifest,
                slot,
                manifestPath,
                Selection: selection,
                Error: "活动运行时槽缺少 desktop-runtime.json。");

        RuntimeManifest manifest;
        try
        {
            manifest = RuntimeManifest.Load(manifestPath);
        }
        catch (Exception ex)
        {
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.InvalidManifest,
                slot,
                manifestPath,
                Selection: selection,
                Error: ex.Message);
        }

        if (!string.Equals(manifest.RuntimeId, runtimeId, StringComparison.Ordinal))
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.InvalidManifest,
                slot,
                manifestPath,
                manifest,
                selection,
                Error: "manifest runtimeId 与槽目录不一致。");

        var issues = verifyFiles ? manifest.Verify(slot) : manifest.ValidateMetadata();
        if (issues.Count > 0)
            return new RuntimeSlotResolution(
                RuntimeSlotStatus.IntegrityFailed,
                slot,
                manifestPath,
                manifest,
                selection,
                issues,
                "运行时完整性校验失败。");

        return new RuntimeSlotResolution(
            RuntimeSlotStatus.Ready,
            slot,
            manifestPath,
            manifest,
            selection,
            issues);
    }

    public RuntimeSlotActivationResult Activate(string runtimeId)
    {
        var candidate = Resolve(runtimeId, verifyFiles: true);
        if (!candidate.IsReady)
            return new RuntimeSlotActivationResult(
                false,
                Error: candidate.Error ?? candidate.Status.ToString());

        RuntimeSlotSelection? current = null;
        var active = ResolveActive(verifyFiles: false);
        if (active.IsReady) current = active.Selection;
        var previous = current?.ActiveRuntimeId == runtimeId
            ? current.PreviousRuntimeId
            : current?.ActiveRuntimeId;
        var selection = new RuntimeSlotSelection(1, runtimeId, previous);
        try
        {
            WriteSelection(selection);
            return new RuntimeSlotActivationResult(true, selection);
        }
        catch (Exception ex)
        {
            return new RuntimeSlotActivationResult(false, Error: ex.Message);
        }
    }

    public RuntimeSlotActivationResult Rollback()
    {
        var current = ResolveActive(verifyFiles: false);
        var previous = current.Selection?.PreviousRuntimeId;
        if (!current.IsReady || string.IsNullOrWhiteSpace(previous))
            return new RuntimeSlotActivationResult(false, Error: "没有可回退的已验证运行时槽。");
        return Activate(previous);
    }

    public RuntimeSlotQuarantineResult QuarantineInactive(string runtimeId)
    {
        if (!IsSafeRuntimeId(runtimeId))
            return new RuntimeSlotQuarantineResult(false, Error: "runtimeId 含不允许的路径字符。");

        var current = ResolveActive(verifyFiles: false);
        if (current.Selection == null)
            return new RuntimeSlotQuarantineResult(false, Error: "缺少有效的活动槽选择。");
        if (string.Equals(current.Selection.ActiveRuntimeId, runtimeId, StringComparison.Ordinal))
            return new RuntimeSlotQuarantineResult(false, Error: "不能隔离当前活动运行时槽。");

        var source = Path.Combine(_runtimeRoot, VersionsDirectoryName, runtimeId);
        if (!Directory.Exists(source))
            return new RuntimeSlotQuarantineResult(false, Error: "待隔离的运行时槽不存在。");

        try
        {
            var rejectedRoot = Path.Combine(_runtimeRoot, "rejected");
            Directory.CreateDirectory(rejectedRoot);
            var destination = Path.Combine(
                rejectedRoot,
                runtimeId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.Move(source, destination);
            if (string.Equals(current.Selection.PreviousRuntimeId, runtimeId, StringComparison.Ordinal))
            {
                WriteSelection(current.Selection with { PreviousRuntimeId = null });
            }
            return new RuntimeSlotQuarantineResult(true, destination);
        }
        catch (Exception ex)
        {
            return new RuntimeSlotQuarantineResult(false, Error: ex.Message);
        }
    }

    /// <summary>
    /// Retains the active verified slot and one verified rollback slot. All other verified,
    /// inactive slots may be removed. Staging directories are removed only after their minimum
    /// age; recent work and anything that is not a normal directory is left untouched.
    /// </summary>
    public RuntimeStoreMaintenanceResult PruneInactiveAndStaging(
        TimeSpan? minimumStagingAge = null,
        DateTimeOffset? now = null)
    {
        var retained = new List<string>();
        var removedSlots = new List<string>();
        var removedStaging = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var age = minimumStagingAge ?? TimeSpan.FromHours(1);
        if (age < TimeSpan.Zero)
        {
            errors.Add("staging 保留时间不能为负数。");
            return new RuntimeStoreMaintenanceResult(false, retained, removedSlots, removedStaging, skipped, errors);
        }

        var active = ResolveActive(verifyFiles: true);
        if (!active.IsReady || active.Selection == null)
        {
            errors.Add("活动运行时未通过完整性校验，拒绝执行清理。");
            return new RuntimeStoreMaintenanceResult(false, retained, removedSlots, removedStaging, skipped, errors);
        }

        var selection = active.Selection;
        var versionsRoot = Path.Combine(_runtimeRoot, VersionsDirectoryName);
        var verified = new List<(string RuntimeId, DateTime LastWriteUtc)>();
        try
        {
            if (Directory.Exists(versionsRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(versionsRoot))
                {
                    var info = new DirectoryInfo(directory);
                    var runtimeId = info.Name;
                    if (!IsSafeRuntimeId(runtimeId))
                    {
                        skipped.Add(directory);
                        continue;
                    }
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        skipped.Add(directory);
                        continue;
                    }

                    var resolution = Resolve(runtimeId, verifyFiles: true);
                    if (!resolution.IsReady)
                    {
                        skipped.Add(directory);
                        continue;
                    }
                    verified.Add((runtimeId, info.LastWriteTimeUtc));
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("枚举运行时槽失败：" + ex.Message);
            return new RuntimeStoreMaintenanceResult(false, retained, removedSlots, removedStaging, skipped, errors);
        }

        var activeEntry = verified.FirstOrDefault(slot =>
            string.Equals(slot.RuntimeId, selection.ActiveRuntimeId, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(activeEntry.RuntimeId))
        {
            errors.Add("活动运行时槽未出现在已验证槽列表中，拒绝执行清理。");
            return new RuntimeStoreMaintenanceResult(false, retained, removedSlots, removedStaging, skipped, errors);
        }

        retained.Add(selection.ActiveRuntimeId);
        var rollback = verified.FirstOrDefault(slot =>
            !string.Equals(slot.RuntimeId, selection.ActiveRuntimeId, StringComparison.Ordinal)
            && string.Equals(slot.RuntimeId, selection.PreviousRuntimeId, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(rollback.RuntimeId))
        {
            rollback = verified
                .Where(slot => !string.Equals(slot.RuntimeId, selection.ActiveRuntimeId, StringComparison.Ordinal))
                .OrderByDescending(slot => slot.LastWriteUtc)
                .FirstOrDefault();
        }

        var rollbackId = string.IsNullOrWhiteSpace(rollback.RuntimeId) ? null : rollback.RuntimeId;
        if (rollbackId != null) retained.Add(rollbackId);
        if (!string.Equals(selection.PreviousRuntimeId, rollbackId, StringComparison.Ordinal))
        {
            try
            {
                selection = selection with { PreviousRuntimeId = rollbackId };
                WriteSelection(selection);
            }
            catch (Exception ex)
            {
                errors.Add("更新回退槽指针失败，未删除任何槽：" + ex.Message);
                return new RuntimeStoreMaintenanceResult(false, retained, removedSlots, removedStaging, skipped, errors);
            }
        }

        foreach (var slot in verified)
        {
            if (retained.Contains(slot.RuntimeId, StringComparer.Ordinal)) continue;
            var directory = Path.Combine(versionsRoot, slot.RuntimeId);
            try
            {
                Directory.Delete(directory, recursive: true);
                removedSlots.Add(slot.RuntimeId);
            }
            catch (Exception ex)
            {
                errors.Add("删除旧运行时槽 " + slot.RuntimeId + " 失败：" + ex.Message);
            }
        }

        var stagingRoot = Path.Combine(_runtimeRoot, StagingDirectoryName);
        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - age;
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                        || info.LastWriteTimeUtc > cutoff)
                    {
                        skipped.Add(directory);
                        continue;
                    }
                    try
                    {
                        Directory.Delete(directory, recursive: true);
                        removedStaging.Add(directory);
                    }
                    catch (Exception ex)
                    {
                        errors.Add("删除过期 staging 目录失败：" + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("枚举 staging 目录失败：" + ex.Message);
        }

        return new RuntimeStoreMaintenanceResult(
            errors.Count == 0,
            retained,
            removedSlots,
            removedStaging,
            skipped,
            errors);
    }

    /// <summary>
    /// Bounds forensic storage for candidates previously quarantined after a failed health gate.
    /// The newest <paramref name="maximumRetained"/> entries are retained, as is every normal
    /// directory within <paramref name="minimumEvidenceAge"/>. Reparse points and unexpected
    /// names are never deleted automatically.
    /// </summary>
    public RuntimeRejectedMaintenanceResult PruneRejected(
        int maximumRetained = 3,
        TimeSpan? minimumEvidenceAge = null,
        DateTimeOffset? now = null)
    {
        var retained = new List<string>();
        var removed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var age = minimumEvidenceAge ?? TimeSpan.FromDays(7);
        if (maximumRetained < 0)
        {
            errors.Add("The rejected runtime retention count cannot be negative.");
            return new RuntimeRejectedMaintenanceResult(false, retained, removed, skipped, errors);
        }
        if (age < TimeSpan.Zero)
        {
            errors.Add("The rejected runtime evidence age cannot be negative.");
            return new RuntimeRejectedMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var rejectedRoot = Path.Combine(_runtimeRoot, "rejected");
        var candidates = new List<DirectoryInfo>();
        try
        {
            if (!Directory.Exists(rejectedRoot))
                return new RuntimeRejectedMaintenanceResult(true, retained, removed, skipped, errors);
            foreach (var directory in Directory.EnumerateDirectories(rejectedRoot))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0
                    || !SafeRejectedDirectoryName.IsMatch(info.Name))
                {
                    skipped.Add(directory);
                    continue;
                }
                candidates.Add(info);
            }
        }
        catch (Exception ex)
        {
            errors.Add("Unable to enumerate rejected runtime candidates: " + ex.Message);
            return new RuntimeRejectedMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - age;
        foreach (var candidate in candidates.OrderByDescending(entry => entry.LastWriteTimeUtc)
                     .ThenByDescending(entry => entry.Name, StringComparer.Ordinal)
                     .Select((entry, index) => (entry, index)))
        {
            if (candidate.index < maximumRetained || candidate.entry.LastWriteTimeUtc > cutoff)
            {
                retained.Add(candidate.entry.FullName);
                continue;
            }
            try
            {
                Directory.Delete(candidate.entry.FullName, recursive: true);
                removed.Add(candidate.entry.FullName);
            }
            catch (Exception ex)
            {
                errors.Add("Unable to delete rejected runtime candidate " + candidate.entry.Name + ": " + ex.Message);
            }
        }

        return new RuntimeRejectedMaintenanceResult(
            errors.Count == 0,
            retained,
            removed,
            skipped,
            errors);
    }

    private void WriteSelection(RuntimeSlotSelection selection)
    {
        Directory.CreateDirectory(_runtimeRoot);
        var temporary = SelectionPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(selection, JsonOptions(writeIndented: true)) + "\n";
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, SelectionPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
        }
    }

    public static bool IsSafeRuntimeId(string? runtimeId)
        => runtimeId != null && SafeRuntimeId.IsMatch(runtimeId);

    private static JsonSerializerOptions JsonOptions(bool writeIndented = false)
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented,
        };
}
