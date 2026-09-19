using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeReleaseFeedClientTests
{
    [Fact]
    public async Task FetchVerifiedPayload_WritesOnlyVerifiedPayloadToDestination()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = Encoding.UTF8.GetBytes("verified runtime archive");
        var descriptor = Descriptor(payload);
        var handler = new RouteHandler()
            .Add(MetadataUri, () => JsonResponse(Sign(descriptor, key)))
            .Add(descriptor.PayloadUrl, () => BytesResponse(payload));
        using var http = new HttpClient(handler);

        var result = await new RuntimeReleaseFeedClient(http).FetchVerifiedPayloadAsync(
            MetadataUri,
            Trusted(key),
            "beta",
            Path.Combine(temp.Path, "runtime.zip"));

        Assert.True(result.Success);
        Assert.Equal(payload, File.ReadAllBytes(result.PayloadPath!));
        Assert.Equal(2, handler.RequestedUris.Count);
    }

    [Fact]
    public async Task FetchVerifiedPayload_RejectsUnknownMetadataKeyBeforePayloadRequest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var descriptor = Descriptor(Encoding.UTF8.GetBytes("payload"));
        var handler = new RouteHandler().Add(MetadataUri, () => JsonResponse(Sign(descriptor, key)));
        using var http = new HttpClient(handler);

        var result = await new RuntimeReleaseFeedClient(http).FetchVerifiedPayloadAsync(
            MetadataUri,
            new Dictionary<string, string>(),
            "beta",
            Path.Combine(temp.Path, "runtime.zip"));

        Assert.Equal(RuntimeReleaseDownloadStatus.MetadataRejected, result.Status);
        Assert.Single(handler.RequestedUris);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task FetchVerifiedPayload_RejectsChannelMismatchBeforePayloadRequest()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var descriptor = Descriptor(Encoding.UTF8.GetBytes("payload"));
        var handler = new RouteHandler().Add(MetadataUri, () => JsonResponse(Sign(descriptor, key)));
        using var http = new HttpClient(handler);

        var result = await new RuntimeReleaseFeedClient(http).FetchVerifiedPayloadAsync(
            MetadataUri,
            Trusted(key),
            "stable",
            Path.Combine(temp.Path, "runtime.zip"));

        Assert.Equal(RuntimeReleaseDownloadStatus.ChannelMismatch, result.Status);
        Assert.Single(handler.RequestedUris);
    }

    [Fact]
    public async Task FetchVerifiedPayload_DeletesMismatchedPayloadInsteadOfPublishingIt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var descriptor = Descriptor(Encoding.UTF8.GetBytes("expected"));
        var handler = new RouteHandler()
            .Add(MetadataUri, () => JsonResponse(Sign(descriptor, key)))
            .Add(descriptor.PayloadUrl, () => BytesResponse(Encoding.UTF8.GetBytes("tampered")));
        using var http = new HttpClient(handler);

        var result = await new RuntimeReleaseFeedClient(http).FetchVerifiedPayloadAsync(
            MetadataUri,
            Trusted(key),
            "beta",
            Path.Combine(temp.Path, "runtime.zip"));

        Assert.Equal(RuntimeReleaseDownloadStatus.PayloadIntegrityFailed, result.Status);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task FetchVerifiedPayload_EnforcesPayloadSizeLimitBeforePublishing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = Encoding.UTF8.GetBytes("payload longer than limit");
        var descriptor = Descriptor(payload);
        var handler = new RouteHandler()
            .Add(MetadataUri, () => JsonResponse(Sign(descriptor, key)))
            .Add(descriptor.PayloadUrl, () => BytesResponse(payload));
        using var http = new HttpClient(handler);

        var result = await new RuntimeReleaseFeedClient(http)
        {
            MaximumPayloadBytes = 4,
        }.FetchVerifiedPayloadAsync(
            MetadataUri,
            Trusted(key),
            "beta",
            Path.Combine(temp.Path, "runtime.zip"));

        Assert.Equal(RuntimeReleaseDownloadStatus.PayloadTooLarge, result.Status);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    private static Uri MetadataUri { get; } = new("https://updates.example.invalid/runtime.json");

    private static RuntimeReleaseDescriptor Descriptor(byte[] payload) => new(
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
        PayloadUrl: "https://updates.example.invalid/dsh-runtime.zip",
        PayloadSha256: Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
        IssuedAtUtc: DateTimeOffset.Parse("2026-09-19T00:00:00Z"),
        ExpiresAtUtc: DateTimeOffset.Parse("2026-09-26T00:00:00Z"));

    private static SignedRuntimeRelease Sign(RuntimeReleaseDescriptor descriptor, ECDsa key) => new(
        descriptor,
        Convert.ToBase64String(key.SignData(
            RuntimeReleaseTrust.CreateSigningPayload(descriptor),
            HashAlgorithmName.SHA256)));

    private static IReadOnlyDictionary<string, string> Trusted(ECDsa key) => new Dictionary<string, string>
    {
        ["release-2026"] = key.ExportSubjectPublicKeyInfoPem(),
    };

    private static HttpResponseMessage JsonResponse(SignedRuntimeRelease release)
        => new(HttpStatusCode.OK) { Content = new StringContent(RuntimeReleaseTrust.Serialize(release)) };

    private static HttpResponseMessage BytesResponse(byte[] bytes)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        internal List<string> RequestedUris { get; } = [];

        internal RouteHandler Add(Uri uri, Func<HttpResponseMessage> response)
            => Add(uri.AbsoluteUri, response);

        internal RouteHandler Add(string uri, Func<HttpResponseMessage> response)
        {
            _responses.Add(uri, response);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            RequestedUris.Add(uri);
            return Task.FromResult(_responses.TryGetValue(uri, out var response)
                ? response()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
