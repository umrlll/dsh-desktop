using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record RuntimeReleaseFeedConfigurationIssue(string Code, string? Detail = null);

/// <summary>
/// Deployment-owned configuration for the signed runtime release feed. The configuration carries
/// only public trust roots; it is invalid by default and rejects placeholder, non-HTTPS, or
/// non-P-256 values before a caller can construct a <see cref="RuntimeReleaseSource"/>.
/// </summary>
public sealed record RuntimeReleaseFeedConfiguration
{
    private static readonly Regex SafeKeyId = new(
        @"^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public int SchemaVersion { get; init; } = 1;
    public string MetadataUrl { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public Dictionary<string, string> TrustedPublicKeys { get; init; } = [];

    public static RuntimeReleaseFeedConfiguration Parse(string json)
        => JsonSerializer.Deserialize<RuntimeReleaseFeedConfiguration>(json, JsonOptions)
            ?? throw new InvalidDataException("Release feed configuration is empty.");

    public string Serialize()
        => JsonSerializer.Serialize(this, JsonOptions);

    public IReadOnlyList<RuntimeReleaseFeedConfigurationIssue> Validate()
    {
        var issues = new List<RuntimeReleaseFeedConfigurationIssue>();
        if (SchemaVersion != 1) issues.Add(new RuntimeReleaseFeedConfigurationIssue("schema-version"));
        if (!Uri.TryCreate(MetadataUrl, UriKind.Absolute, out var metadata)
            || !string.Equals(metadata.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(metadata.UserInfo))
        {
            issues.Add(new RuntimeReleaseFeedConfigurationIssue("metadata-url"));
        }
        if (Channel is not "stable" and not "beta")
            issues.Add(new RuntimeReleaseFeedConfigurationIssue("channel"));
        if (TrustedPublicKeys.Count == 0 || TrustedPublicKeys.Count > 8)
            issues.Add(new RuntimeReleaseFeedConfigurationIssue("trusted-key-count"));
        foreach (var pair in TrustedPublicKeys)
        {
            if (!SafeKeyId.IsMatch(pair.Key))
            {
                issues.Add(new RuntimeReleaseFeedConfigurationIssue("key-id", pair.Key));
                continue;
            }
            if (!IsP256PublicKey(pair.Value))
                issues.Add(new RuntimeReleaseFeedConfigurationIssue("public-key", pair.Key));
        }
        return issues;
    }

    public RuntimeReleaseSource ToSource()
    {
        var issues = Validate();
        if (issues.Count > 0)
            throw new InvalidDataException("Invalid release feed configuration: " + string.Join(
                ", ", issues.Select(issue => issue.Code + (issue.Detail == null ? string.Empty : "(" + issue.Detail + ")"))));
        return new RuntimeReleaseSource(
            new Uri(MetadataUrl, UriKind.Absolute),
            Channel,
            new Dictionary<string, string>(TrustedPublicKeys, StringComparer.Ordinal));
    }

    private static bool IsP256PublicKey(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            var curve = key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
            return string.Equals(curve, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
