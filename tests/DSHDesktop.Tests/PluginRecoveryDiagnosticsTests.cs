using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class PluginRecoveryDiagnosticsTests
{
    [Fact]
    public void Analyze_CorrelatesNewBundleWithLockfilePackageAndStderr()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var bundle = "@example/broken-plugin";
        File.WriteAllText(Path.Combine(temp.Path, "pnpm-lock.yaml"), "packages:\n  " + bundle + ": {}\n");
        Directory.CreateDirectory(Path.Combine(temp.Path, "node_modules", "@example", "broken-plugin"));

        var result = PluginRecoveryDiagnostics.Analyze(
            temp.Path,
            ["@deepseek-ai/dsh-base", bundle],
            ["@deepseek-ai/dsh-base"],
            ["loader failed to initialize " + bundle]);

        var candidate = Assert.Single(result);
        Assert.Equal(bundle, candidate.Bundle);
        Assert.Equal(1, candidate.LoaderIndex);
        Assert.Equal(
            ["bundle-added-after-healthy", "lockfile-reference", "installed-package", "recent-stderr-reference"],
            candidate.Evidence.Select(evidence => evidence.Code));
    }

    [Fact]
    public void Analyze_UsesOnlyLastNonCoreBundleWhenThereIsNoHealthyBaselineDifference()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();

        var result = PluginRecoveryDiagnostics.Analyze(
            temp.Path,
            ["@deepseek-ai/dsh-base", "first-plugin", "last-plugin"],
            ["@deepseek-ai/dsh-base", "first-plugin", "last-plugin"]);

        var candidate = Assert.Single(result);
        Assert.Equal("last-plugin", candidate.Bundle);
        Assert.Contains(candidate.Evidence, evidence => evidence.Code == "bundle-last-loader-entry");
    }

    [Fact]
    public void Analyze_IgnoresUnsafeAndProtectedBundleNames()
    {
        using var temp = new RuntimeSlotManagerTests.TempDirectory();

        var result = PluginRecoveryDiagnostics.Analyze(
            temp.Path,
            ["@deepseek-ai/dsh-base", "../escape", "@deepseek-ai/dsh-web-app"],
            []);

        Assert.Empty(result);
    }
}
