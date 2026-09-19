using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeManagerTests
{
    [Fact]
    public void Resolve_PrefersCompleteBundledRuntime()
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path);
        var bundledNode = Path.Combine(options.ApplicationBaseDirectory, "runtime", "node", "node.exe");
        var bundledPackage = Path.Combine(
            options.ApplicationBaseDirectory,
            "runtime", "dsh", "node_modules", "@deepseek-ai", "dsh");
        var bundledEntry = Path.Combine(bundledPackage, "lib", "bin.js");
        Touch(bundledNode);
        Touch(bundledEntry);
        Touch(Path.Combine(bundledPackage, "package.json"), "{\"version\":\"0.1.6-alpha.2\"}");

        var manager = new RuntimeManager(options);
        var result = manager.Resolve();

        Assert.True(result.CanLaunch);
        Assert.True(result.IsBundled);
        Assert.Equal(bundledNode, result.NodePath);
        Assert.Equal(bundledEntry, result.DshEntryPath);
        Assert.Equal(Path.Combine(options.ApplicationBaseDirectory, "runtime", "dsh"), result.InstallRoot);
        Assert.Empty(result.Issues);
        Assert.False(result.IsVerified);
        Assert.Equal(Path.Combine(options.ApplicationBaseDirectory, "runtime"), result.RuntimeRoot);
        Assert.Equal("0.1.6-alpha.2", manager.ReadDshVersion(result));
    }

    [Fact]
    public void Resolve_UsesVerifiedActiveRuntimeSlot()
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path);
        var runtimeRoot = Path.Combine(options.ApplicationBaseDirectory, "runtime");
        RuntimeSlotManagerTests.CreateSlot(runtimeRoot, "runtime-a", "a");
        Assert.True(new RuntimeSlotManager(runtimeRoot).Activate("runtime-a").Success);

        var result = new RuntimeManager(options).Resolve();

        Assert.True(result.CanLaunch);
        Assert.True(result.IsBundled);
        Assert.True(result.IsVerified);
        Assert.Equal("runtime-a", result.RuntimeId);
        Assert.Contains(Path.Combine("runtime", "versions", "runtime-a"), result.NodePath);
        Assert.Equal(runtimeRoot, result.RuntimeStateRoot);
        Assert.Equal(
            Path.Combine(options.LocalApplicationDataDirectory, "DSHDesktop", "runtime"),
            new RuntimeManager(options).WritableRuntimeRoot);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Resolve_PrefersWritableActiveSlotOverInstalledRuntime()
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path);
        var installedRoot = Path.Combine(options.ApplicationBaseDirectory, "runtime");
        RuntimeSlotManagerTests.CreateSlot(installedRoot, "runtime-installed", "installed");
        Assert.True(new RuntimeSlotManager(installedRoot).Activate("runtime-installed").Success);
        var writableRoot = Path.Combine(options.LocalApplicationDataDirectory, "DSHDesktop", "runtime");
        RuntimeSlotManagerTests.CreateSlot(writableRoot, "runtime-updated", "updated");
        Assert.True(new RuntimeSlotManager(writableRoot).Activate("runtime-updated").Success);

        var result = new RuntimeManager(options).Resolve();

        Assert.Equal("runtime-updated", result.RuntimeId);
        Assert.Equal(writableRoot, result.RuntimeStateRoot);
        Assert.Contains(Path.Combine("runtime", "versions", "runtime-updated"), result.NodePath);
    }

    [Fact]
    public void Resolve_DamagedActiveSlotBlocksExternalFallback()
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path);
        var runtimeRoot = Path.Combine(options.ApplicationBaseDirectory, "runtime");
        var slot = RuntimeSlotManagerTests.CreateSlot(runtimeRoot, "runtime-a", "a");
        Assert.True(new RuntimeSlotManager(runtimeRoot).Activate("runtime-a").Success);
        File.WriteAllText(
            Path.Combine(slot, "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"),
            "tampered-and-longer");

        var fallbackNode = Path.Combine(sandbox.Path, "external", "node.exe");
        Touch(fallbackNode);
        var npxPackage = Path.Combine(
            options.LocalApplicationDataDirectory,
            "npm-cache", "_npx", options.NpxCacheKey,
            "node_modules", "@deepseek-ai", "dsh");
        Touch(Path.Combine(npxPackage, "lib", "bin.js"));
        var configured = options with { NodeOverride = fallbackNode };

        var result = new RuntimeManager(configured).Resolve();

        Assert.False(result.CanLaunch);
        Assert.Null(result.NodePath);
        Assert.Null(result.DshEntryPath);
        Assert.Contains(result.Issues, issue => issue.Code == RuntimeIssueCode.RuntimeIntegrityFailed);
    }

    [Fact]
    public void Resolve_UsesExplicitNodeAndNpxFallback()
    {
        using var sandbox = new TemporaryDirectory();
        var overrideNode = Path.Combine(sandbox.Path, "override", "node.exe");
        Touch(overrideNode);
        var options = Options(sandbox.Path) with { NodeOverride = overrideNode };
        var npxPackage = Path.Combine(
            options.LocalApplicationDataDirectory,
            "npm-cache", "_npx", options.NpxCacheKey,
            "node_modules", "@deepseek-ai", "dsh");
        Touch(Path.Combine(npxPackage, "lib", "bin.js"));
        Touch(Path.Combine(npxPackage, "package.json"), "{\"version\":\"2.0.0\"}");

        var result = new RuntimeManager(options).Resolve();

        Assert.Equal(RuntimeSource.EnvironmentOverride, result.NodeSource);
        Assert.Equal(RuntimeSource.NpxCache, result.DshSource);
        Assert.Equal(overrideNode, result.NodePath);
        Assert.Equal(Path.Combine(options.LocalApplicationDataDirectory, "npm-cache", "_npx", options.NpxCacheKey), result.InstallRoot);
    }

    [Fact]
    public void Resolve_FindsNodeOnConfiguredPath()
    {
        using var sandbox = new TemporaryDirectory();
        var nodeDirectory = Path.Combine(sandbox.Path, "path-node");
        var node = Path.Combine(nodeDirectory, "node.exe");
        Touch(node);
        var options = Options(sandbox.Path) with { PathValue = nodeDirectory };

        var result = new RuntimeManager(options).Resolve();

        Assert.Equal(node, result.NodePath);
        Assert.Equal(RuntimeSource.Path, result.NodeSource);
        Assert.Contains(result.Issues, issue => issue.Code == RuntimeIssueCode.MissingDshEntry);
    }

    [Fact]
    public void Resolve_ReturnsStableMissingIssues()
    {
        using var sandbox = new TemporaryDirectory();

        var result = new RuntimeManager(Options(sandbox.Path)).Resolve();

        Assert.False(result.CanLaunch);
        Assert.Equal(RuntimeSource.Missing, result.NodeSource);
        Assert.Equal(RuntimeSource.Missing, result.DshSource);
        Assert.Collection(
            result.Issues,
            issue => Assert.Equal(RuntimeIssueCode.MissingNode, issue.Code),
            issue => Assert.Equal(RuntimeIssueCode.MissingDshEntry, issue.Code));
    }

    [Fact]
    public void Resolve_ReportsMissingPackageManifestWithoutBlockingLaunch()
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path);
        Touch(Path.Combine(options.ApplicationBaseDirectory, "runtime", "node", "node.exe"));
        Touch(Path.Combine(
            options.ApplicationBaseDirectory,
            "runtime", "dsh", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));

        var result = new RuntimeManager(options).Resolve();

        Assert.True(result.CanLaunch);
        Assert.Contains(result.Issues, issue => issue.Code == RuntimeIssueCode.MissingPackageManifest);
        Assert.Null(new RuntimeManager(options).ReadDshVersion(result));
    }

    [Fact]
    public void FindNpm_ReturnsMatchingNodeAndCli()
    {
        using var sandbox = new TemporaryDirectory();
        var node = Path.Combine(sandbox.Path, "known", "node.exe");
        var npmCli = Path.Combine(sandbox.Path, "known", "node_modules", "npm", "bin", "npm-cli.js");
        Touch(node);
        Touch(npmCli);
        var options = Options(sandbox.Path) with { KnownNodePaths = new[] { node } };

        var npm = new RuntimeManager(options).FindNpm();

        Assert.NotNull(npm);
        Assert.Equal(node, npm.NodePath);
        Assert.Equal(npmCli, npm.NpmCliPath);
    }

    [Fact]
    public void ReadDshVersion_ReturnsNullForDamagedManifest()
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path);
        Touch(Path.Combine(options.ApplicationBaseDirectory, "runtime", "node", "node.exe"));
        var package = Path.Combine(
            options.ApplicationBaseDirectory,
            "runtime", "dsh", "node_modules", "@deepseek-ai", "dsh");
        Touch(Path.Combine(package, "lib", "bin.js"));
        Touch(Path.Combine(package, "package.json"), "not-json");
        var manager = new RuntimeManager(options);

        Assert.Null(manager.ReadDshVersion());
    }

    [Fact]
    public void EntryPathHelpers_DerivePackageAndInstallRoots()
    {
        var entry = Path.Combine("C:\\runtime", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");

        Assert.Equal(
            Path.Combine("C:\\runtime", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            RuntimeManager.PackageManifestForEntry(entry));
        Assert.Equal("C:\\runtime", RuntimeManager.InstallRootForEntry(entry));
    }

    private static RuntimeSearchOptions Options(string root)
        => new(
            Path.Combine(root, "app"),
            Path.Combine(root, "local"),
            NodeOverride: null,
            PathValue: null,
            KnownNodePaths: Array.Empty<string>())
        {
            NodeExecutableName = "node.exe",
        };

    private static void Touch(string path, string content = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dshdesktop-runtime-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the assertion result.
            }
        }
    }
}
