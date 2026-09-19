using System.Security.Cryptography;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeReleasePublisherTests
{
    [Fact]
    public void Sign_ProducesMetadataAcceptedByConfiguredP256TrustRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var descriptor = Descriptor();

        var signed = RuntimeReleasePublisher.Sign(descriptor, key.ExportPkcs8PrivateKeyPem());
        var verification = RuntimeReleaseTrust.Verify(signed, new Dictionary<string, string>
        {
            [descriptor.KeyId] = key.ExportSubjectPublicKeyInfoPem(),
        }, descriptor.IssuedAtUtc);

        Assert.True(verification.IsVerified);
        Assert.Equal(descriptor, signed.Descriptor);
    }

    [Fact]
    public void Sign_RejectsPrivateKeysThatAreNotP256()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<CryptographicException>(() => RuntimeReleasePublisher.Sign(
            Descriptor(),
            key.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void Sign_RefusesDescriptorThatClientsWouldReject()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var invalid = Descriptor() with { PayloadUrl = "http://updates.example.invalid/runtime.zip" };

        Assert.Throws<InvalidDataException>(() => RuntimeReleasePublisher.Sign(
            invalid,
            key.ExportPkcs8PrivateKeyPem()));
    }

    private static RuntimeReleaseDescriptor Descriptor()
    {
        var issued = DateTimeOffset.UtcNow;
        return new RuntimeReleaseDescriptor(
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
            PayloadUrl: "https://updates.example.invalid/runtime.zip",
            PayloadSha256: new string('a', 64),
            IssuedAtUtc: issued,
            ExpiresAtUtc: issued.AddDays(7));
    }
}
