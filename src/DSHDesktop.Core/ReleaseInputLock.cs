using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record ReleaseInputArtifact(string Url, string Sha256);

public sealed record ReleaseInputLockIssue(string Code, string? Detail = null);

/// <summary>
/// Deployment-owned provenance lock for the three runtime inputs used by a Portable release.
/// The lock names only HTTPS artifacts and their SHA-256 digests; a caller must additionally
/// prove that its identity is a verified, publishable combination in the compatibility matrix.
/// </summary>
public sealed record ReleaseInputLock
{
    private static readonly Regex ExactVersion = new(
        @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new(
        @"^[0-9a-f]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int SchemaVersion { get; init; } = 1;
    public string Channel { get; init; } = string.Empty;
    public string RuntimeId { get; init; } = string.Empty;
    public string DesktopVersion { get; init; } = string.Empty;
    public string DshVersion { get; init; } = string.Empty;
    public string NodeVersion { get; init; } = string.Empty;
    public string PnpmVersion { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProfileSchema { get; init; }
    public ReleaseInputArtifact? Node { get; init; }
    public ReleaseInputArtifact? Dsh { get; init; }
    public ReleaseInputArtifact? Pnpm { get; init; }

    public static ReleaseInputLock Parse(string json)
        => JsonSerializer.Deserialize<ReleaseInputLock>(json, JsonOptions)
            ?? throw new InvalidDataException("Release input lock is empty.");

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public IReadOnlyList<ReleaseInputLockIssue> Validate()
    {
        var issues = new List<ReleaseInputLockIssue>();
        if (SchemaVersion != 1) issues.Add(new("schema-version"));
        if (Channel is not ("stable" or "beta")) issues.Add(new("channel"));
        if (!RuntimeSlotManager.IsSafeRuntimeId(RuntimeId)) issues.Add(new("runtime-id"));
        ValidateVersion(DesktopVersion, "desktop-version", issues);
        ValidateVersion(DshVersion, "dsh-version", issues);
        ValidateVersion(NodeVersion, "node-version", issues);
        ValidateVersion(PnpmVersion, "pnpm-version", issues);
        if (Platform != "win32") issues.Add(new("platform"));
        if (Architecture != "x64") issues.Add(new("architecture"));
        if (ProfileSchema < 1) issues.Add(new("profile-schema"));
        ValidateArtifact(Node, "node", issues);
        ValidateArtifact(Dsh, "dsh", issues);
        ValidateArtifact(Pnpm, "pnpm", issues);
        return issues;
    }

    public IReadOnlyList<ReleaseInputLockIssue> ValidateAgainst(CompatibilityMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var issues = new List<ReleaseInputLockIssue>(Validate());
        if (matrix.Validate().Count > 0)
        {
            issues.Add(new ReleaseInputLockIssue("compatibility-matrix-invalid"));
            return issues;
        }
        var matched = matrix.Combinations.Any(combination =>
            combination.Status == "verified"
            && combination.Publishable
            && combination.Channels.Contains(Channel, StringComparer.Ordinal)
            && string.Equals(combination.RuntimeId, RuntimeId, StringComparison.Ordinal)
            && string.Equals(combination.DesktopVersion, DesktopVersion, StringComparison.Ordinal)
            && string.Equals(combination.DshVersion, DshVersion, StringComparison.Ordinal)
            && string.Equals(combination.NodeVersion, NodeVersion, StringComparison.Ordinal)
            && string.Equals(combination.PnpmVersion, PnpmVersion, StringComparison.Ordinal)
            && string.Equals(combination.Platform, Platform, StringComparison.Ordinal)
            && string.Equals(combination.Architecture, Architecture, StringComparison.Ordinal)
            && combination.ProfileSchema == ProfileSchema);
        if (!matched) issues.Add(new ReleaseInputLockIssue("compatibility-not-publishable"));
        return issues;
    }

    public void EnsureValidAgainst(CompatibilityMatrix matrix)
    {
        var issues = ValidateAgainst(matrix);
        if (issues.Count > 0)
            throw new InvalidDataException("Invalid release input lock: " + string.Join(", ", issues.Select(issue => issue.Code)));
    }

    private static void ValidateVersion(string value, string code, ICollection<ReleaseInputLockIssue> issues)
    {
        if (!ExactVersion.IsMatch(value ?? string.Empty)) issues.Add(new ReleaseInputLockIssue(code));
    }

    private static void ValidateArtifact(ReleaseInputArtifact? artifact, string name, ICollection<ReleaseInputLockIssue> issues)
    {
        if (artifact == null)
        {
            issues.Add(new ReleaseInputLockIssue(name));
            return;
        }
        if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
            issues.Add(new ReleaseInputLockIssue(name + "-url"));
        if (!Sha256.IsMatch(artifact.Sha256 ?? string.Empty))
            issues.Add(new ReleaseInputLockIssue(name + "-sha256"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
