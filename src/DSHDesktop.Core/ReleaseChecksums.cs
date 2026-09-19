using System.Security.Cryptography;
using System.Text;

namespace DSHDesktop.Core;

public sealed record ReleaseChecksumEntry(string Path, long Size, string Sha256);

public sealed record ReleaseChecksumIssue(string Code, string? Path = null);

/// <summary>
/// Deterministic SHA-256 inventory for release assets such as installers and portable ZIP files.
/// The checksum document is intentionally separate from the runtime manifest: it signs neither
/// files nor publishers, but makes every release asset independently verifiable.
/// </summary>
public sealed class ReleaseChecksums
{
    private List<ReleaseChecksumEntry> _entries = [];

    public List<ReleaseChecksumEntry> Entries { get => _entries; init => _entries = value ?? []; }

    public static ReleaseChecksums Create(string root, string? excludedPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException(fullRoot);
        var excluded = string.IsNullOrWhiteSpace(excludedPath)
            ? null
            : Path.GetFullPath(excludedPath);
        return new ReleaseChecksums
        {
            Entries = Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFullPath(path), excluded, StringComparison.OrdinalIgnoreCase))
                .Select(path => new ReleaseChecksumEntry(
                    NormalizeRelativePath(Path.GetRelativePath(fullRoot, path)),
                    new FileInfo(path).Length,
                    HashFile(path)))
                .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public IReadOnlyList<ReleaseChecksumIssue> Verify(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        var issues = new List<ReleaseChecksumIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            if (!TryNormalizeRelativePath(entry.Path, out var path)
                || entry.Size < -1
                || !IsSha256(entry.Sha256)
                || !seen.Add(path))
            {
                issues.Add(new ReleaseChecksumIssue("invalid-entry", entry.Path));
                continue;
            }
            var absolute = Path.GetFullPath(Path.Combine(
                fullRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithinRoot(fullRoot, absolute))
            {
                issues.Add(new ReleaseChecksumIssue("path-escape", entry.Path));
                continue;
            }
            if (!File.Exists(absolute))
            {
                issues.Add(new ReleaseChecksumIssue("missing", entry.Path));
                continue;
            }
            var info = new FileInfo(absolute);
            if (entry.Size >= 0 && info.Length != entry.Size)
                issues.Add(new ReleaseChecksumIssue("size", entry.Path));
            if (!string.Equals(HashFile(absolute), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                issues.Add(new ReleaseChecksumIssue("sha256", entry.Path));
        }
        return issues;
    }

    public static ReleaseChecksums Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new List<ReleaseChecksumEntry>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length == 0) continue;
            var separator = line.IndexOf("  *", StringComparison.Ordinal);
            if (separator != 64 || line.Length <= separator + 3)
                throw new InvalidDataException("Invalid SHA256SUMS line.");
            var path = line[(separator + 3)..];
            entries.Add(new ReleaseChecksumEntry(path, Size: -1, Sha256: line[..separator]));
        }
        return new ReleaseChecksums { Entries = entries };
    }

    /// <summary>Writes GNU-compatible SHA256SUMS lines atomically. Sizes remain in-memory metadata.</summary>
    public void Write(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutput = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullOutput)
            ?? throw new InvalidDataException("Checksum output path has no parent directory.");
        Directory.CreateDirectory(parent);
        var ordered = Entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToList();
        if (ordered.Any(entry => !TryNormalizeRelativePath(entry.Path, out _) || !IsSha256(entry.Sha256)))
            throw new InvalidDataException("Checksum entries are invalid.");
        var temporary = fullOutput + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var text = string.Concat(ordered.Select(entry => entry.Sha256.ToLowerInvariant() + "  *" + entry.Path + "\n"));
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, fullOutput, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        if (!TryNormalizeRelativePath(path, out var normalized))
            throw new InvalidDataException("Release asset path is invalid: " + path);
        return normalized;
    }

    private static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var candidate = path.Replace('\\', '/');
        if (candidate.StartsWith("/", StringComparison.Ordinal)
            || candidate.Contains("//", StringComparison.Ordinal)) return false;
        var segments = candidate.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")) return false;
        normalized = string.Join('/', segments);
        return true;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256(string? value)
        => value?.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
