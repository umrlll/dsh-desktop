using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record RuntimeManifestIdentity(
    string RuntimeId,
    string DesktopVersion,
    string DshVersion,
    string NodeVersion,
    string PnpmVersion,
    string Platform,
    string Architecture,
    int ProfileSchema);

public sealed record RuntimeManifestFile(string Path, long Size, string Sha256);

public sealed record RuntimeManifestIssue(string Code, string Message, string? Path = null);

/// <summary>
/// Deterministic file manifest for one immutable runtime slot. Paths are normalized to forward
/// slashes, sorted ordinally, and verified without trusting paths outside the supplied root.
/// </summary>
public sealed class RuntimeManifest
{
    public const string FileName = "desktop-runtime.json";

    private static readonly Regex ExactVersion = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private List<RuntimeManifestFile> _files = [];

    public int SchemaVersion { get; init; } = 1;
    public string RuntimeId { get; init; } = string.Empty;
    public string DesktopVersion { get; init; } = string.Empty;
    public string DshVersion { get; init; } = string.Empty;
    public string NodeVersion { get; init; } = string.Empty;
    public string PnpmVersion { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProfileSchema { get; init; }
    public List<RuntimeManifestFile> Files { get => _files; init => _files = value ?? []; }

    public static RuntimeManifest Create(string root, RuntimeManifestIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(identity);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException("运行时根目录不存在: " + fullRoot);

        var manifest = new RuntimeManifest
        {
            RuntimeId = identity.RuntimeId,
            DesktopVersion = identity.DesktopVersion,
            DshVersion = identity.DshVersion,
            NodeVersion = identity.NodeVersion,
            PnpmVersion = identity.PnpmVersion,
            Platform = identity.Platform,
            Architecture = identity.Architecture,
            ProfileSchema = identity.ProfileSchema,
            Files = Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                .Select(path => new
                {
                    FullPath = path,
                    RelativePath = NormalizeRelativePath(Path.GetRelativePath(fullRoot, path)),
                })
                .Where(file => !string.Equals(file.RelativePath, FileName, StringComparison.Ordinal))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .Select(file => ManifestFile(file.FullPath, file.RelativePath))
                .ToList(),
        };

        var metadataIssues = manifest.ValidateMetadata();
        if (metadataIssues.Count > 0)
            throw new InvalidDataException(string.Join(
                Environment.NewLine,
                metadataIssues.Select(issue => issue.Code + ": " + issue.Message)));
        return manifest;
    }

    public static RuntimeManifest Load(string path)
        => JsonSerializer.Deserialize<RuntimeManifest>(
            File.ReadAllText(path),
            JsonOptions()) ?? throw new InvalidDataException("运行时 manifest 为空。");

    public void Write(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("manifest 输出路径缺少父目录。");
        Directory.CreateDirectory(parent);
        var temporary = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions(writeIndented: true)) + "\n";
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
        }
    }

    public IReadOnlyList<RuntimeManifestIssue> Verify(string root)
    {
        var issues = new List<RuntimeManifestIssue>(ValidateMetadata());
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
        {
            issues.Add(new("runtime-root-missing", "运行时根目录不存在。", fullRoot));
            return issues;
        }

        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!TryResolveFile(fullRoot, file.Path, out var fullPath))
            {
                issues.Add(new("unsafe-path", "manifest 文件路径不是安全的规范化相对路径。", file.Path));
                continue;
            }
            if (!declared.Add(file.Path))
            {
                issues.Add(new("duplicate-path", "manifest 文件路径重复。", file.Path));
                continue;
            }
            if (!File.Exists(fullPath))
            {
                issues.Add(new("missing-file", "manifest 声明的文件不存在。", file.Path));
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != file.Size)
                issues.Add(new("size-mismatch", $"文件大小应为 {file.Size}，实际为 {info.Length}。", file.Path));
            var actualHash = HashFile(fullPath);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add(new("hash-mismatch", "文件 SHA-256 不匹配。", file.Path));
        }

        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(fullRoot, path));
            if (string.Equals(relative, FileName, StringComparison.Ordinal)) continue;
            if (!declared.Contains(relative))
                issues.Add(new("unexpected-file", "运行时包含 manifest 未声明的文件。", relative));
        }

        return issues;
    }

    public IReadOnlyList<RuntimeManifestIssue> ValidateMetadata()
    {
        var issues = new List<RuntimeManifestIssue>();
        if (SchemaVersion != 1) issues.Add(new("manifest-schema", "schemaVersion 必须为 1。"));
        if (string.IsNullOrWhiteSpace(RuntimeId)) issues.Add(new("runtime-id", "runtimeId 不能为空。"));
        ValidateVersion(DesktopVersion, "desktopVersion", issues);
        ValidateVersion(DshVersion, "dshVersion", issues);
        ValidateVersion(NodeVersion, "nodeVersion", issues);
        ValidateVersion(PnpmVersion, "pnpmVersion", issues);
        if (Platform is not ("win32" or "darwin" or "linux"))
            issues.Add(new("platform", "platform 必须是 win32、darwin 或 linux。"));
        if (Architecture is not ("x64" or "arm64"))
            issues.Add(new("architecture", "architecture 必须是 x64 或 arm64。"));
        if (ProfileSchema < 1) issues.Add(new("profile-schema", "profileSchema 必须大于等于 1。"));
        if (Files.Count == 0) issues.Add(new("empty-files", "运行时 manifest 至少需要一个文件。"));
        return issues;
    }

    private static RuntimeManifestFile ManifestFile(string fullPath, string relativePath)
    {
        var info = new FileInfo(fullPath);
        return new RuntimeManifestFile(relativePath, info.Length, HashFile(fullPath));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string NormalizeRelativePath(string path) => path.Replace('\\', '/');

    private static bool TryResolveFile(string fullRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\')
            || relativePath.Split('/').Any(part => part is "" or "." or ".."))
            return false;

        try
        {
            var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateVersion(
        string version,
        string field,
        ICollection<RuntimeManifestIssue> issues)
    {
        if (!ExactVersion.IsMatch(version ?? string.Empty))
            issues.Add(new("exact-version", field + " 必须是精确版本，不能使用范围或通配符。"));
    }

    private static JsonSerializerOptions JsonOptions(bool writeIndented = false)
        => new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = writeIndented,
        };
}
