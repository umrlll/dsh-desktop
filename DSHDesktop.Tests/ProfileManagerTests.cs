using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ProfileManagerTests
{
    [Fact]
    public void Resolve_BuildsSeparateNormalAndSafeDescriptors()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));

        var normal = manager.Resolve(ProfileMode.Normal);
        var safe = manager.Resolve(ProfileMode.Safe);

        Assert.Equal("web", normal.Name);
        Assert.Equal(ProfileMode.Normal, normal.Mode);
        Assert.Equal(Path.Combine(sandbox.Path, "user", ".dsh", "profiles", "web"), normal.Directory);
        Assert.Equal(Path.Combine(normal.DshHome, "settings.yaml"), normal.SettingsPath);
        Assert.Equal("desktop-safe", safe.Name);
        Assert.Equal(ProfileMode.Safe, safe.Mode);
        Assert.Equal("web", safe.InitializationTemplate);
        Assert.Equal(Path.Combine(sandbox.Path, "user", ".dsh-desktop-safe"), safe.DshHome);
        Assert.NotEqual(normal.DshHome, safe.DshHome);
        Assert.NotEqual(normal.Directory, safe.Directory);
        Assert.Equal(normal, manager.Active);
        Assert.Equal("desktop", manager.Desktop.Name);
        Assert.Equal("web", manager.Desktop.InitializationTemplate);
    }

    [Fact]
    public void Activate_SwitchesBetweenNormalAndSafeProfiles()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));

        manager.Activate(ProfileMode.Safe);
        Assert.Equal("desktop-safe", manager.Active.Name);

        manager.Activate(ProfileMode.Normal);
        Assert.Equal("web", manager.Active.Name);
    }

    [Fact]
    public void Resolve_UsesExplicitDshHome()
    {
        using var sandbox = new TemporaryDirectory();
        var overridden = Path.Combine(sandbox.Path, "custom-home");
        var manager = new ProfileManager(Options(sandbox.Path) with { DshHomeOverride = overridden });

        var profile = manager.Active;

        Assert.Equal(overridden, profile.DshHome);
        Assert.Equal(Path.Combine(overridden, "profiles", "web"), profile.Directory);
    }

    [Fact]
    public void ResolveSafe_UsesExplicitIsolatedHome()
    {
        using var sandbox = new TemporaryDirectory();
        var safeHome = Path.Combine(sandbox.Path, "isolated-safe-home");
        var manager = new ProfileManager(Options(sandbox.Path) with
        {
            SafeDshHomeOverride = safeHome,
        });

        var safe = manager.Resolve(ProfileMode.Safe);

        Assert.Equal(safeHome, safe.DshHome);
        Assert.Equal(Path.Combine(safeHome, "profiles", "desktop-safe"), safe.Directory);
        Assert.Equal(Path.Combine(safeHome, "settings.yaml"), safe.SettingsPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("nested/profile")]
    public void Constructor_RejectsUnsafeProfileNames(string name)
    {
        using var sandbox = new TemporaryDirectory();
        var options = Options(sandbox.Path) with { NormalProfileName = name };

        Assert.Throws<ArgumentException>(() => new ProfileManager(options));
    }

    [Fact]
    public void Initialize_ReturnsNoSeedWhenManifestIsAbsent()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));

        var result = manager.Initialize(manager.Active);

        Assert.Equal(ProfileInitializationStatus.NoSeed, result.Status);
        Assert.False(Directory.Exists(manager.Active.Directory));
    }

    [Fact]
    public void Initialize_HonorsDisabledSeedOption()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path) with { DisableSeed = true });
        Touch(Path.Combine(manager.Active.SeedRoot, "package.json"), "{}");

        var result = manager.Initialize(manager.Active);

        Assert.Equal(ProfileInitializationStatus.Disabled, result.Status);
        Assert.False(Directory.Exists(manager.Active.Directory));
    }

    [Fact]
    public void Initialize_NeverSeedsSafeMode()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));
        var safe = manager.Resolve(ProfileMode.Safe);
        Touch(Path.Combine(safe.SeedRoot, "package.json"), "{}");

        var result = manager.Initialize(safe);

        Assert.Equal(ProfileInitializationStatus.SafeModeSkipped, result.Status);
        Assert.False(Directory.Exists(safe.Directory));
    }

    [Fact]
    public void Initialize_DoesNotModifyAnyExistingProfileDirectory()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));
        Touch(Path.Combine(manager.Active.SeedRoot, "package.json"), "{\"seed\":true}");
        Touch(Path.Combine(manager.Active.Directory, "user-file.txt"), "keep");

        var result = manager.Initialize(manager.Active);

        Assert.Equal(ProfileInitializationStatus.Existing, result.Status);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(manager.Active.Directory, "user-file.txt")));
        Assert.False(File.Exists(Path.Combine(manager.Active.Directory, "package.json")));
    }

    [Fact]
    public void Initialize_SeedsAtomicallyAndSkipsMachineSpecificMetadata()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));
        var seed = manager.Active.SeedRoot;
        Touch(Path.Combine(seed, "package.json"), "{\"name\":\"seed\"}");
        Touch(Path.Combine(seed, "node_modules", "plugin-a", "index.js"), "ok");
        Touch(Path.Combine(seed, "node_modules", ".modules.yaml"), "machine-path");
        Touch(Path.Combine(seed, "node_modules", ".pnpm", "metadata"), "skip");
        Touch(Path.Combine(seed, "node_modules", ".bin", "plugin-a.cmd"), "skip");

        var first = manager.Initialize(manager.Active);
        var second = manager.Initialize(manager.Active);

        Assert.Equal(ProfileInitializationStatus.Seeded, first.Status);
        Assert.True(first.Seeded);
        Assert.Equal(ProfileInitializationStatus.Existing, second.Status);
        Assert.True(File.Exists(Path.Combine(manager.Active.Directory, "package.json")));
        Assert.True(File.Exists(Path.Combine(manager.Active.Directory, "node_modules", "plugin-a", "index.js")));
        Assert.False(File.Exists(Path.Combine(manager.Active.Directory, "node_modules", ".modules.yaml")));
        Assert.False(Directory.Exists(Path.Combine(manager.Active.Directory, "node_modules", ".pnpm")));
        Assert.False(Directory.Exists(Path.Combine(manager.Active.Directory, "node_modules", ".bin")));
        Assert.Empty(Directory.GetDirectories(
            Path.GetDirectoryName(manager.Active.Directory)!,
            Path.GetFileName(manager.Active.Directory) + ".seed-*"));
    }

    [Fact]
    public void MigrateLegacyToDesktop_CopiesOnlyPortableFilesAndPreservesSource()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));
        var source = manager.Active.Directory;
        Touch(Path.Combine(source, "package.json"), "{\"name\":\"legacy\"}");
        Touch(Path.Combine(source, "cordis.patch.yml"), "- id: user-setting");
        Touch(Path.Combine(source, "pnpm-lock.yaml"), "lockfileVersion: '9.0'");
        Touch(Path.Combine(source, "node_modules", "plugin-a", "index.js"), "machine-specific");
        Touch(Path.Combine(source, "package.json.bak"), "backup");

        var result = manager.MigrateLegacyToDesktop();

        Assert.True(result.Migrated);
        Assert.Equal(ProfileMigrationStatus.Migrated, result.Status);
        Assert.Equal(new[] { "package.json", "cordis.patch.yml", "pnpm-lock.yaml" }, result.CopiedFiles);
        Assert.True(File.Exists(Path.Combine(manager.Desktop.Directory, "package.json")));
        Assert.True(File.Exists(Path.Combine(manager.Desktop.Directory, "cordis.patch.yml")));
        Assert.False(Directory.Exists(Path.Combine(manager.Desktop.Directory, "node_modules")));
        Assert.False(File.Exists(Path.Combine(manager.Desktop.Directory, "package.json.bak")));
        Assert.True(File.Exists(Path.Combine(source, "node_modules", "plugin-a", "index.js")));
    }

    [Fact]
    public void MigrateLegacyToDesktop_NeverOverwritesExistingTarget()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));
        Touch(Path.Combine(manager.Active.Directory, "package.json"), "{}");
        Touch(Path.Combine(manager.Desktop.Directory, "keep.txt"), "keep");

        var result = manager.MigrateLegacyToDesktop();

        Assert.Equal(ProfileMigrationStatus.TargetExists, result.Status);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(manager.Desktop.Directory, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(manager.Desktop.Directory, "package.json")));
    }

    [Fact]
    public void MigrateLegacyToDesktop_RejectsMissingOrInvalidSource()
    {
        using var sandbox = new TemporaryDirectory();
        var manager = new ProfileManager(Options(sandbox.Path));

        var missing = manager.MigrateLegacyToDesktop();
        Touch(Path.Combine(manager.Active.Directory, "package.json"), "not-json");
        var invalid = manager.MigrateLegacyToDesktop();

        Assert.Equal(ProfileMigrationStatus.SourceMissing, missing.Status);
        Assert.Equal(ProfileMigrationStatus.SourceInvalid, invalid.Status);
        Assert.False(Directory.Exists(manager.Desktop.Directory));
    }

    private static ProfileManagerOptions Options(string root)
        => new(
            DshHomeOverride: null,
            UserProfileDirectory: Path.Combine(root, "user"),
            ApplicationBaseDirectory: Path.Combine(root, "app"));

    private static void Touch(string path, string content)
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
                "dshdesktop-profile-tests",
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
