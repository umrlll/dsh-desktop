using Xunit;

namespace DSHDesktop.Tests;

public class InstallerContractTests
{
    [Fact]
    public void InnoInstaller_IsPerUserAndRejectsShellOnlyPublishRoots()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, "installer", "DSHDesktop.iss"));

        Assert.Contains("DefaultDirName={localappdata}\\Programs\\DSHDesktop", source);
        Assert.Contains("PrivilegesRequired=lowest", source);
        Assert.Contains("runtime\\active.json", source);
        Assert.Contains("SourceDir is missing runtime", source);
        Assert.Contains("LICENSE", source);
        Assert.Contains("NOTICE.md", source);
        Assert.DoesNotContain("[UninstallDelete]", source);
    }
}
