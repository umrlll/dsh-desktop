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
    public Dictionary<string, string> ChannelMetadataUrls { get; init; } = [];
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
        if (Channel is not "stable" and not "beta")
        {
            issues.Add(new RuntimeReleaseFeedConfigurationIssue("channel"));
        }
        if (ChannelMetadataUrls.Count == 0)
        {
            if (!IsHttpsUrl(MetadataUrl))
                issues.Add(new RuntimeReleaseFeedConfigurationIssue("metadata-url"));
        }
        else
        {
            if (ChannelMetadataUrls.Count > 2 || !ChannelMetadataUrls.ContainsKey(Channel))
                issues.Add(new RuntimeReleaseFeedConfigurationIssue("channel-metadata-urls"));
            foreach (var pair in ChannelMetadataUrls)
            {
                if (pair.Key is not ("stable" or "beta") || !IsHttpsUrl(pair.Value))
                    issues.Add(new RuntimeReleaseFeedConfigurationIssue("channel-metadata-url", pair.Key));
            }
        }
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

    public RuntimeReleaseSource ToSource(string? channel = null)
    {
        var issues = Validate();
        if (issues.Count > 0)
            throw new InvalidDataException("Invalid release feed configuration: " + string.Join(
                ", ", issues.Select(issue => issue.Code + (issue.Detail == null ? string.Empty : "(" + issue.Detail + ")"))));
        var requestedChannel = string.IsNullOrWhiteSpace(channel) ? Channel : channel.Trim();
        if (requestedChannel is not ("stable" or "beta")
            || (ChannelMetadataUrls.Count == 0 && !string.Equals(requestedChannel, Channel, StringComparison.Ordinal))
            || (ChannelMetadataUrls.Count > 0 && !ChannelMetadataUrls.ContainsKey(requestedChannel)))
        {
            throw new InvalidDataException("The requested release channel is not configured.");
        }
        var metadataUrl = ChannelMetadataUrls.Count == 0
            ? MetadataUrl
            : ChannelMetadataUrls[requestedChannel];
        return new RuntimeReleaseSource(
            new Uri(metadataUrl, UriKind.Absolute),
            requestedChannel,
            new Dictionary<string, string>(TrustedPublicKeys, StringComparer.Ordinal));
    }

    /// <summary>
    /// Loads an explicitly supplied deployment configuration and converts it to a validated
    /// release source. A missing, malformed, or linked file never produces a partially trusted
    /// source, allowing a desktop host to keep its updater fail-closed until deployment has
    /// supplied a real feed and pinned public key.
    /// </summary>
    public static bool TryLoadSourceFile(
        string? configurationPath,
        out RuntimeReleaseSource? source,
        out string? error)
    {
        if (!TryLoadConfigurationFile(configurationPath, out var configuration, out error)
            || configuration == null)
        {
            source = null;
            return false;
        }
        try
        {
            source = configuration.ToSource();
            return true;
        }
        catch (InvalidDataException)
        {
            source = null;
            error = "configuration-invalid";
            return false;
        }
    }

    public static bool TryLoadConfigurationFile(
        string? configurationPath,
        out RuntimeReleaseFeedConfiguration? configuration,
        out string? error)
    {
        configuration = null;
        error = null;
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            error = "configuration-path";
            return false;
        }

        try
        {
            var path = Path.GetFullPath(configurationPath);
            if (!File.Exists(path))
            {
                error = "configuration-missing";
                return false;
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                error = "configuration-reparse-point";
                return false;
            }

            configuration = Parse(File.ReadAllText(path));
            if (configuration.Validate().Count > 0)
                throw new InvalidDataException("Release feed configuration is invalid.");
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or CryptographicException)
        {
            error = "configuration-invalid";
            configuration = null;
            return false;
        }
    }

    private static bool IsHttpsUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var metadata)
            && string.Equals(metadata.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(metadata.UserInfo);

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
