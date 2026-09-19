using System.Security.Cryptography;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeReleaseFeedConfigurationTests
{
    [Fact]
    public void Validate_AcceptsHttpsBetaConfigurationWithP256TrustRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = Configuration(key);

        var source = configuration.ToSource();

        Assert.Equal("https://updates.example.invalid/dsh-desktop/beta.json", source.MetadataUri.AbsoluteUri);
        Assert.Equal("beta", source.Channel);
        Assert.Equal(key.ExportSubjectPublicKeyInfoPem(), source.TrustedPublicKeys["release-2026"]);
    }

    [Fact]
    public void Validate_RejectsNonHttpsEndpointAndUnsupportedChannel()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = Configuration(key) with
        {
            MetadataUrl = "http://updates.example.invalid/runtime.json",
            Channel = "dev",
        };

        var issues = configuration.Validate();

        Assert.Contains(issues, issue => issue.Code == "metadata-url");
        Assert.Contains(issues, issue => issue.Code == "channel");
        Assert.Throws<InvalidDataException>(() => configuration.ToSource());
    }

    [Fact]
    public void Validate_RejectsMalformedAndNonP256TrustRoots()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var configuration = new RuntimeReleaseFeedConfiguration
        {
            MetadataUrl = "https://updates.example.invalid/runtime.json",
            Channel = "stable",
            TrustedPublicKeys = new Dictionary<string, string>
            {
                ["bad key"] = "not a pem",
                ["release-p384"] = p384.ExportSubjectPublicKeyInfoPem(),
            },
        };

        var issues = configuration.Validate();

        Assert.Contains(issues, issue => issue.Code == "key-id" && issue.Detail == "bad key");
        Assert.Contains(issues, issue => issue.Code == "public-key" && issue.Detail == "release-p384");
    }

    [Fact]
    public void ParseAndSerialize_RoundTripsDeploymentConfiguration()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var original = Configuration(key);

        var parsed = RuntimeReleaseFeedConfiguration.Parse(original.Serialize());

        Assert.Empty(parsed.Validate());
        Assert.Equal(original.MetadataUrl, parsed.MetadataUrl);
        Assert.Equal(original.Channel, parsed.Channel);
        Assert.Equal(original.TrustedPublicKeys, parsed.TrustedPublicKeys);
    }

    private static RuntimeReleaseFeedConfiguration Configuration(ECDsa key) => new()
    {
        MetadataUrl = "https://updates.example.invalid/dsh-desktop/beta.json",
        Channel = "beta",
        TrustedPublicKeys = new Dictionary<string, string>
        {
            ["release-2026"] = key.ExportSubjectPublicKeyInfoPem(),
        },
    };
}
