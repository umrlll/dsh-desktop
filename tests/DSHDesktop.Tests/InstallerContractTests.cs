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
        Assert.Contains("OutputDir={#OutputDir}", source);
        Assert.DoesNotContain("[UninstallDelete]", source);

        var packager = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, "scripts", "package-installer.ps1"));
        Assert.Contains("verify-portable", packager);
        Assert.Contains("/DSourceDir=", packager);
        Assert.Contains("/DMyAppVersion=", packager);
        Assert.Contains("OutputDir must be outside SourceDir", packager);

        var lifecycle = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, "scripts", "test-installer-lifecycle.ps1"));
        Assert.Contains("/VERYSILENT", lifecycle);
        Assert.Contains("Silent upgrade", lifecycle);
        Assert.Contains("unins*.exe", lifecycle);
        Assert.Contains("retain-after-uninstall.txt", lifecycle);
        Assert.Contains("Get-FileHash", lifecycle);
    }
}
