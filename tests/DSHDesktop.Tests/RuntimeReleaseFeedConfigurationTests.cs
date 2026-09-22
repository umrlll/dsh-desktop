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
    public void ToSource_OnlySelectsChannelsWhoseEndpointsArePinnedInConfiguration()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = Configuration(key) with
        {
            MetadataUrl = string.Empty,
            Channel = "stable",
            ChannelMetadataUrls = new Dictionary<string, string>
            {
                ["stable"] = "https://updates.example.invalid/dsh-desktop/stable.json",
                ["beta"] = "https://updates.example.invalid/dsh-desktop/beta.json",
            },
        };

        Assert.Empty(configuration.Validate());
        Assert.Equal("https://updates.example.invalid/dsh-desktop/stable.json", configuration.ToSource().MetadataUri.AbsoluteUri);
        Assert.Equal("beta", configuration.ToSource("beta").Channel);
        Assert.Throws<InvalidDataException>(() => configuration.ToSource("dev"));
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

    [Fact]
    public void TryLoadSourceFile_FailsClosedUntilAnExplicitValidConfigurationExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-release-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "release-feed.json");
            Assert.False(RuntimeReleaseFeedConfiguration.TryLoadSourceFile(null, out var absent, out var absentError));
            Assert.Null(absent);
            Assert.Equal("configuration-path", absentError);

            Assert.False(RuntimeReleaseFeedConfiguration.TryLoadSourceFile(path, out var missing, out var missingError));
            Assert.Null(missing);
            Assert.Equal("configuration-missing", missingError);

            File.WriteAllText(path, "{ not json }");
            Assert.False(RuntimeReleaseFeedConfiguration.TryLoadSourceFile(path, out var malformed, out var malformedError));
            Assert.Null(malformed);
            Assert.Equal("configuration-invalid", malformedError);

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(path, Configuration(key).Serialize());
            Assert.True(RuntimeReleaseFeedConfiguration.TryLoadSourceFile(path, out var source, out var error));
            Assert.Null(error);
            Assert.NotNull(source);
            Assert.Equal("beta", source.Channel);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
