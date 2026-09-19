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
/// Resolves and atomically switches immutable runtime slots under runtime/versions. A pointer is
/// never updated until the candidate manifest and every declared file have been verified.
/// </summary>
public sealed class RuntimeSlotManager
{
    public const string SelectionFileName = "active.json";
    public const string VersionsDirectoryName = "versions";

    private static readonly Regex SafeRuntimeId = new(
        @"^[0-9A-Za-z][0-9A-Za-z._-]{0,199}$",
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
