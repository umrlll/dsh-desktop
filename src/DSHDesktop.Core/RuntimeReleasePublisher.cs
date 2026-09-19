using System.Security.Cryptography;
using System.Text.Json;

namespace DSHDesktop.Core;

/// <summary>
/// Publisher-side companion to <see cref="RuntimeReleaseTrust"/>. CI supplies the private P-256
/// key at runtime; this type never stores it and verifies its own output before returning it.
/// </summary>
public static class RuntimeReleasePublisher
{
    public static RuntimeReleaseDescriptor ParseDescriptor(string json)
        => JsonSerializer.Deserialize<RuntimeReleaseDescriptor>(json, JsonOptions)
            ?? throw new InvalidDataException("Release descriptor is empty.");

    public static string SerializeDescriptor(RuntimeReleaseDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return JsonSerializer.Serialize(descriptor, JsonOptions);
    }

    public static SignedRuntimeRelease Sign(RuntimeReleaseDescriptor descriptor, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var curve = key.ExportParameters(includePrivateParameters: false).Curve.Oid.Value;
        if (!string.Equals(curve, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal))
            throw new CryptographicException("Runtime release signing requires an ECDSA P-256 key.");
        var release = new SignedRuntimeRelease(
            descriptor,
            Convert.ToBase64String(key.SignData(
                RuntimeReleaseTrust.CreateSigningPayload(descriptor),
                HashAlgorithmName.SHA256)));
        var verification = RuntimeReleaseTrust.Verify(
            release,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [descriptor.KeyId] = key.ExportSubjectPublicKeyInfoPem(),
            },
            descriptor.IssuedAtUtc);
        if (!verification.IsVerified)
            throw new InvalidDataException("Refusing to publish invalid release metadata: " + verification.Error);
        return release;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
