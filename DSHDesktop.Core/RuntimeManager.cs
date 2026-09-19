using System.Text.Json;

namespace DSHDesktop.Core;

public enum RuntimeSource
{
    Missing,
    Bundled,
    EnvironmentOverride,
    KnownLocation,
    Path,
    NpxCache,
}

public enum RuntimeIssueCode
{
    MissingNode,
    MissingDshEntry,
    MissingPackageManifest,
    InvalidRuntimeSelection,
    RuntimeIntegrityFailed,
}

public sealed record RuntimeIssue(RuntimeIssueCode Code, string Message);

public sealed record NpmRuntime(string NodePath, string NpmCliPath);

public sealed record RuntimeResolution(
    string? NodePath,
    RuntimeSource NodeSource,
    string? DshEntryPath,
    RuntimeSource DshSource,
    string? InstallRoot,
    string? PackageManifestPath,
    IReadOnlyList<RuntimeIssue> Issues,
    string? RuntimeId = null,
    string? RuntimeRoot = null,
    string? RuntimeManifestPath = null,
    bool IsVerified = false,
    string? RuntimeStateRoot = null)
{
    public bool CanLaunch => NodePath != null && DshEntryPath != null;
    public bool IsBundled => DshSource == RuntimeSource.Bundled;
}

public sealed record RuntimeSearchOptions(
    string ApplicationBaseDirectory,
    string LocalApplicationDataDirectory,
    string? NodeOverride,
    string? PathValue,
    IReadOnlyList<string> KnownNodePaths,
    string NpxCacheKey = "1e7f6d9597241db0")
{
    public string? RuntimeRootOverride { get; init; }

    public static RuntimeSearchOptions FromEnvironment()
    {
        var executableName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var known = OperatingSystem.IsWindows()
            ? new[]
            {
                @"D:\nodejs\node.exe",
                @"C:\Program Files\nodejs\node.exe",
                @"C:\nodejs\node.exe",
            }
            : Array.Empty<string>();

        return new RuntimeSearchOptions(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("DSH_NODE"),
            Environment.GetEnvironmentVariable("PATH"),
            known)
        {
            NodeExecutableName = executableName,
            RuntimeRootOverride = Environment.GetEnvironmentVariable("DSH_DESKTOP_RUNTIME_ROOT"),
        };
    }

    public string NodeExecutableName { get; init; } = OperatingSystem.IsWindows() ? "node.exe" : "node";
}

public interface IRuntimeManager
{
    string WritableRuntimeRoot { get; }
    RuntimeResolution Resolve();
    NpmRuntime? FindNpm();
    string? ReadDshVersion(RuntimeResolution? resolution = null);
    void Invalidate();
}

/// <summary>
/// Resolves the immutable application runtime first, with explicit development fallbacks.
/// It performs discovery only; downloading, mutation, and slot switching belong to later stages.
/// </summary>
public sealed class RuntimeManager : IRuntimeManager
{
    private const string DshPackageName = "dsh";
    private readonly RuntimeSearchOptions _options;
    private readonly object _sync = new();
    private RuntimeResolution? _verifiedCache;

    public RuntimeManager(RuntimeSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public static RuntimeManager Default { get; } = new(RuntimeSearchOptions.FromEnvironment());

    public string WritableRuntimeRoot => string.IsNullOrWhiteSpace(_options.RuntimeRootOverride)
        ? Path.Combine(_options.LocalApplicationDataDirectory, "DSHDesktop", "runtime")
        : Path.GetFullPath(_options.RuntimeRootOverride);

    public RuntimeResolution Resolve()
    {
        lock (_sync)
            if (_verifiedCache != null) return _verifiedCache;

        var result = ResolveCore();
        if (result.IsVerified && result.CanLaunch)
        {
            lock (_sync) _verifiedCache ??= result;
        }
        return result;
    }

    public void Invalidate()
    {
        lock (_sync) _verifiedCache = null;
    }

    private RuntimeResolution ResolveCore()
    {
        var bundled = ProbeBundledRuntime();
        if (bundled.BlockingIssue != null)
        {
            return new RuntimeResolution(
                null,
                RuntimeSource.Missing,
                null,
                RuntimeSource.Missing,
                bundled.Root,
                null,
                new[] { bundled.BlockingIssue },
                bundled.RuntimeId,
                bundled.Root,
                bundled.ManifestPath,
                IsVerified: false,
                bundled.StateRoot);
        }

        var (nodePath, nodeSource) = bundled.Root == null
            ? FindExternalNode()
            : (Path.Combine(bundled.Root, "node", _options.NodeExecutableName), RuntimeSource.Bundled);
        if (nodeSource == RuntimeSource.Bundled && !File.Exists(nodePath)) nodePath = null;

        var externalDsh = bundled.Root == null
            ? FindExternalDshEntry()
            : (Path: (string?)null, Source: RuntimeSource.Missing);
        var dshEntry = bundled.Root == null
            ? externalDsh.Path
            : Path.Combine(
                bundled.Root,
                "dsh", "node_modules", "@deepseek-ai", DshPackageName, "lib", "bin.js");
        var dshSource = bundled.Root == null ? externalDsh.Source : RuntimeSource.Bundled;
        if (dshSource == RuntimeSource.Bundled && !File.Exists(dshEntry)) dshEntry = null;
        var installRoot = dshEntry == null ? null : InstallRootForEntry(dshEntry);
        var manifest = dshEntry == null ? null : PackageManifestForEntry(dshEntry);
        var issues = new List<RuntimeIssue>();
        if (nodePath == null)
            issues.Add(new RuntimeIssue(RuntimeIssueCode.MissingNode, "未找到可用的 Node.js 运行时。"));
        if (dshEntry == null)
            issues.Add(new RuntimeIssue(RuntimeIssueCode.MissingDshEntry, "未找到 @deepseek-ai/dsh 启动入口。"));
        else if (manifest == null || !File.Exists(manifest))
            issues.Add(new RuntimeIssue(RuntimeIssueCode.MissingPackageManifest, "DSH 运行时缺少 package.json。"));

        return new RuntimeResolution(
            nodePath,
            nodeSource,
            dshEntry,
            dshSource,
            installRoot,
            manifest,
            issues.ToArray(),
            bundled.RuntimeId,
            bundled.Root,
            bundled.ManifestPath,
            bundled.IsVerified,
            bundled.StateRoot);
    }

    public NpmRuntime? FindNpm()
    {
        var resolvedNode = Resolve().NodePath;
        var candidates = _options.KnownNodePaths.Concat(resolvedNode == null
            ? Array.Empty<string>()
            : new[] { resolvedNode });
        foreach (var node in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(node)) continue;
            var nodeDirectory = Path.GetDirectoryName(node);
            if (string.IsNullOrWhiteSpace(nodeDirectory)) continue;
            var cli = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(cli)) return new NpmRuntime(node, cli);
        }

        return null;
    }

    public string? ReadDshVersion(RuntimeResolution? resolution = null)
    {
        var manifest = (resolution ?? Resolve()).PackageManifestPath;
        if (manifest == null || !File.Exists(manifest)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String)
            {
                return version.GetString();
            }
        }
        catch
        {
            // A damaged manifest is reported as an unavailable version, never as a startup crash.
        }

        return null;
    }

    public static string? InstallRootForEntry(string dshEntryPath)
    {
        var directory = Path.GetDirectoryName(dshEntryPath);
        for (var level = 0; level < 4 && directory != null; level++)
            directory = Path.GetDirectoryName(directory);
        return directory;
    }

    public static string? PackageManifestForEntry(string dshEntryPath)
    {
        var libDirectory = Path.GetDirectoryName(dshEntryPath);
        var packageDirectory = libDirectory == null ? null : Path.GetDirectoryName(libDirectory);
        return packageDirectory == null ? null : Path.Combine(packageDirectory, "package.json");
    }

    private (string? Path, RuntimeSource Source) FindExternalNode()
    {
        if (!string.IsNullOrWhiteSpace(_options.NodeOverride) && File.Exists(_options.NodeOverride))
            return (_options.NodeOverride, RuntimeSource.EnvironmentOverride);

        foreach (var candidate in _options.KnownNodePaths)
            if (File.Exists(candidate)) return (candidate, RuntimeSource.KnownLocation);

        foreach (var directory in SplitPath(_options.PathValue))
        {
            try
            {
                var candidate = Path.Combine(directory, _options.NodeExecutableName);
                if (File.Exists(candidate)) return (candidate, RuntimeSource.Path);
            }
            catch
            {
                // Invalid PATH entries are ignored while discovery continues.
            }
        }

        return (null, RuntimeSource.Missing);
    }

    private (string? Path, RuntimeSource Source) FindExternalDshEntry()
    {
        var relative = Path.Combine("node_modules", "@deepseek-ai", DshPackageName, "lib", "bin.js");
        var npx = Path.Combine(
            _options.LocalApplicationDataDirectory,
            "npm-cache",
            "_npx",
            _options.NpxCacheKey,
            relative);
        return File.Exists(npx) ? (npx, RuntimeSource.NpxCache) : (null, RuntimeSource.Missing);
    }

    private BundledRuntimeProbe ProbeBundledRuntime()
    {
        var installedRuntimeRoot = Path.Combine(_options.ApplicationBaseDirectory, "runtime");
        var writableRuntimeRoot = WritableRuntimeRoot;
        var runtimeRoot = !string.IsNullOrWhiteSpace(_options.RuntimeRootOverride)
            ? writableRuntimeRoot
            : File.Exists(Path.Combine(writableRuntimeRoot, RuntimeSlotManager.SelectionFileName))
                ? writableRuntimeRoot
                : installedRuntimeRoot;
        var selectionPath = Path.Combine(runtimeRoot, RuntimeSlotManager.SelectionFileName);
        if (File.Exists(selectionPath))
        {
            var slot = new RuntimeSlotManager(runtimeRoot).ResolveActive(verifyFiles: true);
            if (slot.IsReady)
                return new BundledRuntimeProbe(
                    slot.SlotDirectory,
                    runtimeRoot,
                    slot.Manifest?.RuntimeId,
                    slot.ManifestPath,
                    IsVerified: true);

            var issueCode = slot.Status == RuntimeSlotStatus.IntegrityFailed
                ? RuntimeIssueCode.RuntimeIntegrityFailed
                : RuntimeIssueCode.InvalidRuntimeSelection;
            var detail = slot.Issues is { Count: > 0 }
                ? string.Join("；", slot.Issues.Take(3).Select(issue => issue.Code + (issue.Path == null ? "" : "(" + issue.Path + ")")))
                : slot.Error ?? slot.Status.ToString();
            return new BundledRuntimeProbe(
                slot.SlotDirectory,
                runtimeRoot,
                slot.Selection?.ActiveRuntimeId,
                slot.ManifestPath,
                IsVerified: false,
                new RuntimeIssue(issueCode, "活动运行时不可用：" + detail));
        }

        var flatManifestPath = Path.Combine(runtimeRoot, RuntimeManifest.FileName);
        if (File.Exists(flatManifestPath))
        {
            try
            {
                var manifest = RuntimeManifest.Load(flatManifestPath);
                var issues = manifest.Verify(runtimeRoot);
                if (issues.Count == 0)
                    return new BundledRuntimeProbe(runtimeRoot, runtimeRoot, manifest.RuntimeId, flatManifestPath, IsVerified: true);
                return new BundledRuntimeProbe(
                    runtimeRoot,
                    runtimeRoot,
                    manifest.RuntimeId,
                    flatManifestPath,
                    IsVerified: false,
                    new RuntimeIssue(
                        RuntimeIssueCode.RuntimeIntegrityFailed,
                        "平铺运行时完整性校验失败：" + string.Join("；", issues.Take(3).Select(issue => issue.Code))));
            }
            catch (Exception ex)
            {
                return new BundledRuntimeProbe(
                    runtimeRoot,
                    runtimeRoot,
                    null,
                    flatManifestPath,
                    IsVerified: false,
                    new RuntimeIssue(RuntimeIssueCode.RuntimeIntegrityFailed, "无法读取运行时 manifest：" + ex.Message));
            }
        }

        var legacyNode = Path.Combine(runtimeRoot, "node", _options.NodeExecutableName);
        var legacyDsh = Path.Combine(
            runtimeRoot,
            "dsh", "node_modules", "@deepseek-ai", DshPackageName, "lib", "bin.js");
        return File.Exists(legacyNode) || File.Exists(legacyDsh)
            ? new BundledRuntimeProbe(runtimeRoot, runtimeRoot, null, null, IsVerified: false)
            : new BundledRuntimeProbe(null, runtimeRoot, null, null, IsVerified: false);
    }

    private sealed record BundledRuntimeProbe(
        string? Root,
        string StateRoot,
        string? RuntimeId,
        string? ManifestPath,
        bool IsVerified,
        RuntimeIssue? BlockingIssue = null);

    private static IEnumerable<string> SplitPath(string? value)
        => (value ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
