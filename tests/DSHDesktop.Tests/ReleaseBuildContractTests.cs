using System.IO;
using Xunit;

namespace DSHDesktop.Tests;

public class ReleaseBuildContractTests
{
    private static string ProjectSource()
        => File.ReadAllText(SourceScanner.ProductFile("DSHDesktop.csproj"));

    [Fact]
    public void RuntimeBundle_HasNoMachineSpecificDefaultInputs()
    {
        var source = ProjectSource();

        Assert.DoesNotContain(@"D:\nodejs", source);
        Assert.DoesNotContain("$(LOCALAPPDATA)\\npm-cache", source);
        Assert.DoesNotContain("$(USERPROFILE)\\.dsh", source);
    }

    [Fact]
    public void FullPublish_RequiresAllLockedComponentInputs()
    {
        var source = ProjectSource();

        Assert.Contains("BeforeTargets=\"Publish\"", source);
        foreach (var property in new[]
                 {
                     "BundleNodeSource",
                     "BundleDshSource",
                     "BundlePnpmSource",
                     "BundleNodeVersion",
                     "BundleDshVersion",
                     "BundlePnpmVersion",
                 })
        {
            Assert.Contains("'$(" + property + ")' == ''", source);
        }
    }

    [Fact]
    public void Publish_RebuildsStagingAndRequiresGeneratedManifest()
    {
        var source = ProjectSource();

        Assert.Contains("<RemoveDir Directories=\"$(BundleRuntimeDir)\"", source);
        Assert.Contains("Name=\"GenerateRuntimeManifest\"", source);
        Assert.Contains("DependsOnTargets=\"GenerateRuntimeManifest\"", source);
        Assert.Contains("runtime\\versions\\$(BundleRuntimeId)", source);
        Assert.Contains("runtime\\active.json", source);
        Assert.DoesNotContain("IgnoreExitCode=\"true\" Condition=\"!Exists('$(BundleRuntimeDir)", source);
    }

    [Fact]
    public void Publish_CopiesLicenseAndNoticeIntoReleaseAssets()
    {
        var targets = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, "Directory.Build.targets"));
        var workflow = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("CopyReleaseLegalNotices", targets);
        Assert.Contains("LICENSE;$(MSBuildThisFileDirectory)NOTICE.md", targets);
        Assert.Contains("publish/LICENSE", workflow);
        Assert.Contains("publish/NOTICE.md", workflow);
    }

    [Fact]
    public void PackagedPnpm_PrecedesMachineProbeAndNetworkFallback()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("PnpmSupport.cs"));
        var bundled = source.IndexOf("var bundled = BundledPnpmEntry();", StringComparison.Ordinal);
        var machineProbe = source.IndexOf("MachinePnpmHealthy()", bundled, StringComparison.Ordinal);
        var networkInstall = source.IndexOf("EnsureLocalPnpm(", bundled, StringComparison.Ordinal);

        Assert.True(bundled >= 0, "找不到锁定 pnpm 入口");
        Assert.True(machineProbe > bundled, "锁定 pnpm 必须优先于本机 PATH 探测");
        Assert.True(networkInstall > machineProbe, "联网兜底只能发生在锁定载荷与本机探测之后");
    }
}
