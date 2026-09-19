using System.Security.Cryptography;
using System.Text;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeReleaseTrustTests
{
    [Fact]
    public void Verify_AcceptsTrustedCurrentEcdsaSignature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var release = Sign(Descriptor(), key);

        var result = RuntimeReleaseTrust.Verify(release, new Dictionary<string, string>
        {
            ["release-2026"] = key.ExportSubjectPublicKeyInfoPem(),
        }, DateTimeOffset.Parse("2026-09-20T00:00:00Z"));

        Assert.True(result.IsVerified);
    }

    [Fact]
    public void Verify_RejectsMetadataTamperingAfterSigning()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(Descriptor(), key);
        var tampered = signed with { Descriptor = signed.Descriptor with { DshVersion = "0.1.7-alpha.1" } };

        var result = RuntimeReleaseTrust.Verify(tampered, Trusted(key));

        Assert.Equal(RuntimeReleaseVerificationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void Verify_RejectsUnknownKeyAndExpiredMetadata()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(Descriptor(), key);

        var unknown = RuntimeReleaseTrust.Verify(signed, new Dictionary<string, string>());
        var expired = RuntimeReleaseTrust.Verify(signed, Trusted(key), DateTimeOffset.Parse("2026-10-01T00:00:00Z"));

        Assert.Equal(RuntimeReleaseVerificationStatus.UnknownKey, unknown.Status);
        Assert.Equal(RuntimeReleaseVerificationStatus.Expired, expired.Status);
    }

    [Fact]
    public void Verify_RejectsInsecureOrUnpinnedMetadataBeforeSignatureCheck()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Sign(Descriptor() with { PayloadUrl = "http://example.invalid/runtime.zip" }, key);

        var result = RuntimeReleaseTrust.Verify(signed, Trusted(key));

        Assert.Equal(RuntimeReleaseVerificationStatus.InvalidMetadata, result.Status);
    }

    [Fact]
    public void VerifyPayload_UsesAuthenticatedSha256()
    {
        var bytes = Encoding.UTF8.GetBytes("runtime payload");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var descriptor = Descriptor() with { PayloadSha256 = hash };

        using var valid = new MemoryStream(bytes);
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("different payload"));

        Assert.True(RuntimeReleaseTrust.VerifyPayload(valid, descriptor));
        Assert.False(RuntimeReleaseTrust.VerifyPayload(invalid, descriptor));
    }

    private static RuntimeReleaseDescriptor Descriptor() => new(
        SchemaVersion: 1,
        KeyId: "release-2026",
        Channel: "beta",
        RuntimeId: "dsh-0.1.6-alpha.1-win32-x64-node-24.20.0-pnpm-11.27.0",
        DesktopVersion: "1.0.0",
        DshVersion: "0.1.6-alpha.1",
        NodeVersion: "24.20.0",
        PnpmVersion: "11.27.0",
        Platform: "win32",
        Architecture: "x64",
        ProfileSchema: 1,
        PayloadUrl: "https://downloads.example.invalid/dsh-runtime.zip",
        PayloadSha256: new string('a', 64),
        IssuedAtUtc: DateTimeOffset.Parse("2026-09-19T00:00:00Z"),
        ExpiresAtUtc: DateTimeOffset.Parse("2026-09-26T00:00:00Z"));

    private static SignedRuntimeRelease Sign(RuntimeReleaseDescriptor descriptor, ECDsa key)
        => new(
            descriptor,
            Convert.ToBase64String(key.SignData(
                RuntimeReleaseTrust.CreateSigningPayload(descriptor),
                HashAlgorithmName.SHA256)));

    private static IReadOnlyDictionary<string, string> Trusted(ECDsa key) => new Dictionary<string, string>
    {
        ["release-2026"] = key.ExportSubjectPublicKeyInfoPem(),
    };
}
