namespace DSHDesktop.Core;

public enum ProfileMode
{
    Normal,
    Safe,
}

public enum ProfileInitializationStatus
{
    Existing,
    Seeded,
    NoSeed,
    Disabled,
    SafeModeSkipped,
    Failed,
}

public enum ProfileMigrationStatus
{
    Migrated,
    SourceMissing,
    SourceInvalid,
    TargetExists,
    Failed,
}

public sealed record ProfileDescriptor(
    string Name,
    ProfileMode Mode,
    string DshHome,
    string Directory,
    string SettingsPath,
    string SeedRoot,
    string? InitializationTemplate = null);

public sealed record ProfileInitializationResult(
    ProfileInitializationStatus Status,
    string? Notice = null)
{
    public bool Seeded => Status == ProfileInitializationStatus.Seeded;
}

public sealed record ProfileMigrationResult(
    ProfileMigrationStatus Status,
    string SourceDirectory,
    string TargetDirectory,
    IReadOnlyList<string>? CopiedFiles = null,
    string? Notice = null)
{
    public bool Migrated => Status == ProfileMigrationStatus.Migrated;
}

public sealed record ProfileManagerOptions(
    string? DshHomeOverride,
    string UserProfileDirectory,
    string ApplicationBaseDirectory,
    string NormalProfileName = "web",
    string DesktopProfileName = "desktop",
    string SafeProfileName = "desktop-safe",
    bool DisableSeed = false,
    string? SafeDshHomeOverride = null)
{
    public static ProfileManagerOptions FromEnvironment()
        => new(
            Environment.GetEnvironmentVariable("DSH_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory,
            DisableSeed: Environment.GetEnvironmentVariable("DSHDESKTOP_NO_PROFILE_SEED") == "1",
            SafeDshHomeOverride: Environment.GetEnvironmentVariable("DSHDESKTOP_SAFE_HOME"));
}

public interface IProfileManager
{
    ProfileDescriptor Active { get; }
    ProfileDescriptor Desktop { get; }
    ProfileDescriptor Resolve(ProfileMode mode);
    void Activate(ProfileMode mode);
    ProfileInitializationResult Initialize(ProfileDescriptor profile);
    ProfileMigrationResult MigrateLegacyToDesktop();
}

/// <summary>
/// Owns profile identity, paths, and first-run initialization without depending on a desktop UI.
/// The normal name remains "web" during the compatibility period; migration to "desktop" is an
/// explicit later operation rather than an implicit rename that would hide existing user data.
/// </summary>
public sealed class ProfileManager : IProfileManager
{
    public const string SeedFolderName = "profile-seed";

    private static readonly string[] MigratedProfileFiles =
    {
        "package.json",
        "cordis.patch.yml",
        "pnpm-lock.yaml",
        "pnpm-workspace.yaml",
    };

    private static readonly string[] SkippedSeedNames =
    {
        ".pnpm", ".bin",
        ".modules.yaml", ".package-map.json", ".pnpm-workspace-state-v1.json",
    };

    private readonly ProfileManagerOptions _options;
    private ProfileMode _activeMode;

    public ProfileManager(ProfileManagerOptions options, ProfileMode activeMode = ProfileMode.Normal)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateProfileName(options.NormalProfileName, nameof(options.NormalProfileName));
        ValidateProfileName(options.DesktopProfileName, nameof(options.DesktopProfileName));
        ValidateProfileName(options.SafeProfileName, nameof(options.SafeProfileName));
        _options = options;
        _activeMode = activeMode;
    }

    public static ProfileManager Default { get; } = new(ProfileManagerOptions.FromEnvironment());

    public ProfileDescriptor Active => Resolve(_activeMode);

    public ProfileDescriptor Desktop => ResolveNamed(
        _options.DesktopProfileName,
        ProfileMode.Normal,
        initializationTemplate: "web",
        dshHomeOverride: null);

    public ProfileDescriptor Resolve(ProfileMode mode)
    {
        var name = mode == ProfileMode.Safe
            ? _options.SafeProfileName
            : _options.NormalProfileName;
        return ResolveNamed(
            name,
            mode,
            initializationTemplate: mode == ProfileMode.Safe ? "web" : null,
            dshHomeOverride: mode == ProfileMode.Safe
                ? SafeDshHome()
                : null);
    }

    public void Activate(ProfileMode mode) => _activeMode = mode;

    private ProfileDescriptor ResolveNamed(
        string name,
        ProfileMode mode,
        string? initializationTemplate,
        string? dshHomeOverride)
    {
        var home = string.IsNullOrWhiteSpace(dshHomeOverride)
            ? NormalDshHome()
            : dshHomeOverride!;
        return new ProfileDescriptor(
            name,
            mode,
            home,
            Path.Combine(home, "profiles", name),
            Path.Combine(home, "settings.yaml"),
            Path.Combine(_options.ApplicationBaseDirectory, "runtime", SeedFolderName),
            initializationTemplate);
    }

    private string NormalDshHome()
        => string.IsNullOrWhiteSpace(_options.DshHomeOverride)
            ? Path.Combine(_options.UserProfileDirectory, ".dsh")
            : _options.DshHomeOverride!;

    private string SafeDshHome()
        => string.IsNullOrWhiteSpace(_options.SafeDshHomeOverride)
            ? Path.Combine(_options.UserProfileDirectory, ".dsh-desktop-safe")
            : _options.SafeDshHomeOverride!;

    public ProfileInitializationResult Initialize(ProfileDescriptor profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Mode == ProfileMode.Safe)
            return new ProfileInitializationResult(ProfileInitializationStatus.SafeModeSkipped);
        if (_options.DisableSeed)
            return new ProfileInitializationResult(ProfileInitializationStatus.Disabled);
        if (!File.Exists(Path.Combine(profile.SeedRoot, "package.json")))
            return new ProfileInitializationResult(ProfileInitializationStatus.NoSeed);
        if (Directory.Exists(profile.Directory))
            return new ProfileInitializationResult(ProfileInitializationStatus.Existing);

        var parent = Path.GetDirectoryName(profile.Directory);
        if (string.IsNullOrWhiteSpace(parent))
            return new ProfileInitializationResult(
                ProfileInitializationStatus.Failed,
                "内置插件集成失败：profile 路径缺少父目录。");

        string? staging = null;
        try
        {
            Directory.CreateDirectory(parent);
            staging = profile.Directory + ".seed-" + Guid.NewGuid().ToString("N");
            CopyTree(profile.SeedRoot, staging);
            Directory.Move(staging, profile.Directory);
            staging = null;

            return new ProfileInitializationResult(
                ProfileInitializationStatus.Seeded,
                $"已铺开随应用内置的插件（{profile.Directory}）。");
        }
        catch (Exception ex)
        {
            return new ProfileInitializationResult(
                ProfileInitializationStatus.Failed,
                "内置插件集成失败：" + ex.Message);
        }
        finally
        {
            if (staging != null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch { /* staging cleanup is best effort */ }
            }
        }
    }

    /// <summary>
    /// Copies the supported, portable part of the legacy profile into a new Desktop profile.
    /// The source is never moved or modified, machine-specific node_modules state is excluded,
    /// and an existing destination is never merged or overwritten.
    /// </summary>
    public ProfileMigrationResult MigrateLegacyToDesktop()
    {
        var source = Resolve(ProfileMode.Normal);
        var target = Desktop;
        if (!Directory.Exists(source.Directory))
            return new ProfileMigrationResult(
                ProfileMigrationStatus.SourceMissing,
                source.Directory,
                target.Directory,
                Notice: "未找到现有 web profile，没有可迁移的数据。");
        if (Directory.Exists(target.Directory))
            return new ProfileMigrationResult(
                ProfileMigrationStatus.TargetExists,
                source.Directory,
                target.Directory,
                Notice: "desktop profile 已存在；为避免覆盖，迁移未执行。");

        var sourceManifest = Path.Combine(source.Directory, "package.json");
        if (!IsValidJsonObject(sourceManifest))
            return new ProfileMigrationResult(
                ProfileMigrationStatus.SourceInvalid,
                source.Directory,
                target.Directory,
                Notice: "现有 web profile 缺少有效的 package.json，迁移未执行。");

        var parent = Path.GetDirectoryName(target.Directory);
        if (string.IsNullOrWhiteSpace(parent))
            return new ProfileMigrationResult(
                ProfileMigrationStatus.Failed,
                source.Directory,
                target.Directory,
                Notice: "desktop profile 路径缺少父目录。");

        string? staging = null;
        try
        {
            Directory.CreateDirectory(parent);
            staging = target.Directory + ".migrate-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            var copied = new List<string>();
            foreach (var name in MigratedProfileFiles)
            {
                var sourcePath = Path.Combine(source.Directory, name);
                if (!File.Exists(sourcePath)) continue;
                File.Copy(sourcePath, Path.Combine(staging, name), overwrite: false);
                copied.Add(name);
            }

            Directory.Move(staging, target.Directory);
            staging = null;
            return new ProfileMigrationResult(
                ProfileMigrationStatus.Migrated,
                source.Directory,
                target.Directory,
                copied,
                "已复制可移植 profile 配置；源 web profile 保持不变。外部插件依赖需在切换前恢复。");
        }
        catch (Exception ex)
        {
            return new ProfileMigrationResult(
                ProfileMigrationStatus.Failed,
                source.Directory,
                target.Directory,
                Notice: "desktop profile 迁移失败：" + ex.Message);
        }
        finally
        {
            if (staging != null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
                catch { /* staging cleanup is best effort */ }
            }
        }
    }

    private static bool IsSkipped(string name)
        => SkippedSeedNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!IsSkipped(name)) File.Copy(file, Path.Combine(target, name), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (!IsSkipped(name)) CopyTree(directory, Path.Combine(target, name));
        }
    }

    private static bool IsValidJsonObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateProfileName(string name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("Profile name must be a single valid path segment.", parameterName);
        }
    }
}
