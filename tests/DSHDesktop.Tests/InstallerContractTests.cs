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
        Assert.Contains("InstalledVersion", source);
        Assert.Contains("MyAppId", source);
        Assert.Contains("Downgrades are blocked", source);
        Assert.Contains("cannot be verified", source);
        Assert.Contains("invalid semantic version", source);
        Assert.Contains("CompareVersions", source);
        Assert.Contains("[UninstallDelete]", source);
        Assert.Contains("ShouldPurgeUserData", source);
        Assert.Contains("/PURGEUSERDATA", source);
        Assert.Contains("Type: filesandordirs; Name: \"{localappdata}\\DSHDesktop\"", source);

        var packager = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, "scripts", "package-installer.ps1"));
        Assert.Contains("verify-portable", packager);
        Assert.Contains("expected-desktop-version", packager);
        Assert.Contains("DshDesktopRegistryKey", packager);
        Assert.Contains("MyAppId", packager);
        Assert.Contains("/DSourceDir=", packager);
        Assert.Contains("/DMyAppVersion=", packager);
        Assert.Contains("OutputDir must be outside SourceDir", packager);

        var lifecycle = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot, "scripts", "test-installer-lifecycle.ps1"));
        Assert.Contains("/VERYSILENT", lifecycle);
        Assert.Contains("Silent upgrade", lifecycle);
        Assert.Contains("Silent downgrade rejection", lifecycle);
        Assert.Contains("Invoke-SetupExpectingFailure", lifecycle);
        Assert.Contains("Compare-SemanticVersion", lifecycle);
        Assert.Contains("OlderVersion to be semantically lower", lifecycle);
        Assert.Contains("DSHDesktop-Lifecycle-", lifecycle);
        Assert.Contains("testRegistryKey", lifecycle);
        Assert.Contains("unins*.exe", lifecycle);
        Assert.Contains("retain-after-uninstall.txt", lifecycle);
        Assert.Contains("Get-FileHash", lifecycle);
    }
}
