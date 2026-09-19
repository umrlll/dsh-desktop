using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSHDesktop.Core;

public sealed record ReleaseSbomOptions(
    string DocumentName,
    string Version,
    Uri DocumentNamespace,
    DateTimeOffset CreatedAtUtc);

public sealed record ReleaseSbomChecksum(string Algorithm, string ChecksumValue);

public sealed record ReleaseSbomFile(
    string FileName,
    [property: JsonPropertyName("SPDXID")]
    string SPDXID,
    List<ReleaseSbomChecksum> Checksums,
    string LicenseConcluded = "NOASSERTION",
    string CopyrightText = "NOASSERTION");

public sealed record ReleaseSbomPackage(
    string Name,
    [property: JsonPropertyName("SPDXID")]
    string SPDXID,
    string VersionInfo,
    string DownloadLocation = "NOASSERTION",
    bool FilesAnalyzed = false,
    string LicenseConcluded = "NOASSERTION",
    string LicenseDeclared = "NOASSERTION",
    string CopyrightText = "NOASSERTION");

public sealed record ReleaseSbomRelationship(string SpdxElementId, string RelationshipType, string RelatedSpdxElement);

/// <summary>
/// Minimal deterministic SPDX 2.3 document for a release directory. It inventories every
/// release file by SHA-256 and lists NuGet packages discovered in copied .deps.json files;
/// unavailable license metadata is explicitly represented as NOASSERTION rather than guessed.
/// </summary>
public sealed class ReleaseSbom
{
    public string SpdxVersion { get; init; } = "SPDX-2.3";
    public string DataLicense { get; init; } = "CC0-1.0";
    [JsonPropertyName("SPDXID")]
    public string SPDXID { get; init; } = "SPDXRef-DOCUMENT";
    public string Name { get; init; } = string.Empty;
    public string DocumentNamespace { get; init; } = string.Empty;
    public ReleaseSbomCreationInfo CreationInfo { get; init; } = new();
    public List<ReleaseSbomPackage> Packages { get; init; } = [];
    public List<ReleaseSbomFile> Files { get; init; } = [];
    public List<ReleaseSbomRelationship> Relationships { get; init; } = [];

    public static ReleaseSbom Create(string root, ReleaseSbomOptions options, string? excludedPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException(fullRoot);
        var checksums = ReleaseChecksums.Create(fullRoot, excludedPath).Entries;
        var rootPackage = new ReleaseSbomPackage(
            options.DocumentName,
            "SPDXRef-Package-Root",
            options.Version,
            FilesAnalyzed: true,
            LicenseDeclared: "MIT");
        var packages = new[] { rootPackage }
            .Concat(FindNuGetPackages(fullRoot, excludedPath))
            .ToList();
        var files = checksums.Select(entry => new ReleaseSbomFile(
            "./" + entry.Path,
            "SPDXRef-File-" + entry.Sha256[..16],
            [new ReleaseSbomChecksum("SHA256", entry.Sha256)])).ToList();
        var relationships = new List<ReleaseSbomRelationship>
        {
            new("SPDXRef-DOCUMENT", "DESCRIBES", rootPackage.SPDXID),
        };
        relationships.AddRange(files.Select(file => new ReleaseSbomRelationship(
            rootPackage.SPDXID, "CONTAINS", file.SPDXID)));
        relationships.AddRange(packages.Skip(1).Select(package => new ReleaseSbomRelationship(
            rootPackage.SPDXID, "DEPENDS_ON", package.SPDXID)));
        return new ReleaseSbom
        {
            Name = options.DocumentName,
            DocumentNamespace = options.DocumentNamespace.AbsoluteUri,
            CreationInfo = new ReleaseSbomCreationInfo
            {
                Created = options.CreatedAtUtc.ToUniversalTime().ToString("O"),
                Creators = ["Tool: DSHDesktop.RuntimeTool"],
            },
            Packages = packages,
            Files = files,
            Relationships = relationships,
        };
    }

    public void Write(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var output = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(output)
            ?? throw new InvalidDataException("SBOM output path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
        }
    }

    private static IEnumerable<ReleaseSbomPackage> FindNuGetPackages(string root, string? excludedPath)
    {
        var excluded = string.IsNullOrWhiteSpace(excludedPath) ? null : Path.GetFullPath(excludedPath);
        var packages = new Dictionary<string, ReleaseSbomPackage>(StringComparer.Ordinal);
        foreach (var depsPath in Directory.EnumerateFiles(root, "*.deps.json", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetFullPath(path), excluded, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
                if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                    || libraries.ValueKind != JsonValueKind.Object) continue;
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type)
                        || !string.Equals(type.GetString(), "package", StringComparison.Ordinal)) continue;
                    var separator = library.Name.LastIndexOf('/');
                    if (separator <= 0 || separator == library.Name.Length - 1) continue;
                    var name = library.Name[..separator];
                    var version = library.Name[(separator + 1)..];
                    var id = "SPDXRef-Package-" + Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(library.Name)))[..16];
                    packages.TryAdd(library.Name, new ReleaseSbomPackage(name, id, version));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidDataException("Unable to read release dependency manifest: " + depsPath, ex);
            }
        }
        return packages.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value);
    }

    private static void ValidateOptions(ReleaseSbomOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DocumentName) || options.DocumentName.Any(char.IsControl))
            throw new InvalidDataException("SBOM document name is invalid.");
        if (string.IsNullOrWhiteSpace(options.Version) || options.Version.Any(char.IsControl))
            throw new InvalidDataException("SBOM version is invalid.");
        if (options.DocumentNamespace == null
            || !options.DocumentNamespace.IsAbsoluteUri
            || !string.Equals(options.DocumentNamespace.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(options.DocumentNamespace.UserInfo))
            throw new InvalidDataException("SBOM document namespace must be an HTTPS URI without user info.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

public sealed class ReleaseSbomCreationInfo
{
    public List<string> Creators { get; init; } = [];
    public string Created { get; init; } = string.Empty;
}
