using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class RuntimeReleaseAcquirerTests
{
    [Fact]
    public async Task Acquire_RejectsDescriptorValidatorBeforeExtractingTheVerifiedPayload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        var handler = Routes(descriptor, key, payload.Bytes);
        using var http = new HttpClient(handler);
        var acquirer = new RuntimeReleaseAcquirer(
            new RuntimeReleaseFeedClient(http),
            new RuntimePayloadExtractor());

        var result = await acquirer.AcquireAsync(
            Source(key),
            Path.Combine(temp.Path, "staging"),
            descriptorValidator: _ => "host-contract");

        Assert.Equal(RuntimeReleaseAcquireStatus.ManifestRejected, result.Status);
        Assert.Equal("host-contract", result.Error);
        Assert.Empty(Directory.GetDirectories(Path.Combine(temp.Path, "staging")));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "staging")));
    }

    [Fact]
    public async Task ReleaseUpdater_AcquiresAndActivatesVerifiedRuntimeCandidate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        using var http = new HttpClient(Routes(descriptor, key, payload.Bytes));
        var runtimeRoot = Path.Combine(temp.Path, "runtime");

        var result = await Updater(http, runtimeRoot).AcquireAndActivateAsync(Source(key));

        Assert.True(result.Success);
        Assert.Equal(RuntimeUpdateStageStatus.Activated, result.Adoption?.Status);
        Assert.Equal(descriptor.RuntimeId, new RuntimeSlotManager(runtimeRoot)
            .ResolveActive().Selection?.ActiveRuntimeId);
    }

    [Fact]
    public async Task ReleaseUpdater_AcquiresCandidateWithoutChangingActiveSlotUntilAccepted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        using var http = new HttpClient(Routes(descriptor, key, payload.Bytes));
        var runtimeRoot = Path.Combine(temp.Path, "runtime");
        var updater = Updater(http, runtimeRoot);

        var acquisition = await updater.AcquireAsync(Source(key));

        Assert.True(acquisition.Success);
        Assert.Equal(RuntimeSlotStatus.MissingSelection, new RuntimeSlotManager(runtimeRoot)
            .ResolveActive().Status);

        var activation = updater.ActivateAcquiredCandidate(acquisition);

        Assert.True(activation.Success);
        Assert.Equal(descriptor.RuntimeId, new RuntimeSlotManager(runtimeRoot)
            .ResolveActive().Selection?.ActiveRuntimeId);
    }

    [Fact]
    public async Task ReleaseUpdater_DiscardsUnacceptedCandidateOnlyInsideItsStagingDirectory()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        using var http = new HttpClient(Routes(descriptor, key, payload.Bytes));
        var runtimeRoot = Path.Combine(temp.Path, "runtime");
        var updater = Updater(http, runtimeRoot);
        var acquisition = await updater.AcquireAsync(Source(key));

        Assert.True(acquisition.Success);
        Assert.True(updater.DiscardAcquiredCandidate(acquisition));
        Assert.False(Directory.Exists(acquisition.RuntimeDirectory));

        var outside = acquisition with { RuntimeDirectory = temp.Path };
        Assert.False(updater.DiscardAcquiredCandidate(outside));
        Assert.True(Directory.Exists(temp.Path));
    }

    [Fact]
    public async Task ReleaseUpdater_DoesNotActivateManifestMismatch()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path, manifestDshVersion: "0.1.5-alpha.1");
        var descriptor = Descriptor(payload.Bytes);
        using var http = new HttpClient(Routes(descriptor, key, payload.Bytes));
        var runtimeRoot = Path.Combine(temp.Path, "runtime");

        var result = await Updater(http, runtimeRoot).AcquireAndActivateAsync(Source(key));

        Assert.Equal(RuntimeReleaseUpdateStatus.AcquisitionFailed, result.Status);
        Assert.Null(result.Adoption);
        Assert.Equal(RuntimeSlotStatus.MissingSelection, new RuntimeSlotManager(runtimeRoot)
            .ResolveActive().Status);
    }

    [Fact]
    public async Task ReleaseUpdater_PreservesStagedCandidateOnExistingSlotConflict()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        using var http = new HttpClient(Routes(descriptor, key, payload.Bytes));
        var runtimeRoot = Path.Combine(temp.Path, "runtime");
        RuntimeSlotManagerTests.CreateSlot(runtimeRoot, descriptor.RuntimeId, "different-build");

        var result = await Updater(http, runtimeRoot).AcquireAndActivateAsync(Source(key));

        Assert.Equal(RuntimeReleaseUpdateStatus.CandidateRejected, result.Status);
        Assert.Equal(RuntimeUpdateStageStatus.CandidateConflict, result.Adoption?.Status);
        Assert.Single(Directory.GetDirectories(Path.Combine(
            runtimeRoot, RuntimeSlotManager.StagingDirectoryName), ".runtime-*"));
    }

    [Fact]
    public async Task AcquireAsync_ProducesVerifiedManifestCandidateAndRemovesArchive()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        var handler = Routes(descriptor, key, payload.Bytes);
        using var http = new HttpClient(handler);

        var result = await Acquirer(http).AcquireAsync(Source(key), Path.Combine(temp.Path, "staging"));

        Assert.True(result.Success);
        Assert.NotNull(result.RuntimeDirectory);
        Assert.True(File.Exists(Path.Combine(result.RuntimeDirectory!, RuntimeManifest.FileName)));
        Assert.Empty(Directory.GetFiles(Path.Combine(temp.Path, "staging"), ".release-*"));
        Assert.Equal(RuntimeReleaseDownloadStatus.Downloaded, result.DownloadStatus);
        Assert.Equal(RuntimePayloadExtractionStatus.Extracted, result.ExtractionStatus);
    }

    [Fact]
    public async Task AcquireAsync_RejectsManifestWhoseIdentityDiffersFromSignedDescriptor()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path, manifestDshVersion: "0.1.5-alpha.1");
        var descriptor = Descriptor(payload.Bytes);
        using var http = new HttpClient(Routes(descriptor, key, payload.Bytes));
        var staging = Path.Combine(temp.Path, "staging");

        var result = await Acquirer(http).AcquireAsync(Source(key), staging);

        Assert.Equal(RuntimeReleaseAcquireStatus.ManifestRejected, result.Status);
        Assert.Empty(Directory.GetDirectories(staging, ".runtime-*"));
    }

    [Fact]
    public async Task AcquireAsync_RejectsUnsafeArchiveAndCleansCandidate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreateUnsafePayload(temp.Path);
        var descriptor = Descriptor(payload);
        using var http = new HttpClient(Routes(descriptor, key, payload));
        var staging = Path.Combine(temp.Path, "staging");

        var result = await Acquirer(http).AcquireAsync(Source(key), staging);

        Assert.Equal(RuntimeReleaseAcquireStatus.ExtractionFailed, result.Status);
        Assert.Equal(RuntimePayloadExtractionStatus.UnsafeEntry, result.ExtractionStatus);
        Assert.Empty(Directory.GetDirectories(staging, ".runtime-*"));
        Assert.Empty(Directory.GetFiles(staging, ".release-*"));
    }

    [Fact]
    public async Task AcquireAsync_StopsBeforePayloadWhenTrustRootRejectsMetadata()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var temp = new RuntimeSlotManagerTests.TempDirectory();
        var payload = CreatePayload(temp.Path);
        var descriptor = Descriptor(payload.Bytes);
        var handler = Routes(descriptor, key, payload.Bytes);
        using var http = new HttpClient(handler);

        var result = await Acquirer(http).AcquireAsync(
            Source(new Dictionary<string, string>()),
            Path.Combine(temp.Path, "staging"));

        Assert.Equal(RuntimeReleaseAcquireStatus.InvalidRequest, result.Status);
        Assert.Empty(handler.RequestedUris);
    }

    private static RuntimeReleaseAcquirer Acquirer(HttpClient http)
        => new(new RuntimeReleaseFeedClient(http), new RuntimePayloadExtractor());

    private static RuntimeReleaseUpdater Updater(HttpClient http, string runtimeRoot)
        => new(runtimeRoot, Acquirer(http));

    private static RuntimeReleaseSource Source(ECDsa key) => Source(new Dictionary<string, string>
    {
        ["release-2026"] = key.ExportSubjectPublicKeyInfoPem(),
    });

    private static RuntimeReleaseSource Source(IReadOnlyDictionary<string, string> keys)
        => new(MetadataUri, "beta", keys);

    private static (byte[] Bytes, string Directory) CreatePayload(string root, string manifestDshVersion = "0.1.6-alpha.1")
    {
        var payloadDirectory = Path.Combine(root, "payload");
        Directory.CreateDirectory(Path.Combine(payloadDirectory, "node"));
        Directory.CreateDirectory(Path.Combine(payloadDirectory, "dsh", "node_modules", "@deepseek-ai", "dsh"));
        File.WriteAllText(Path.Combine(payloadDirectory, "node", "node.exe"), "node");
        File.WriteAllText(Path.Combine(payloadDirectory, "dsh", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            "{\"version\":\"" + manifestDshVersion + "\"}");
        var manifest = RuntimeManifest.Create(payloadDirectory, new RuntimeManifestIdentity(
            "dsh-0.1.6-alpha.1-win32-x64-node-24.20.0-pnpm-11.27.0",
            "1.0.0",
            manifestDshVersion,
            "24.20.0",
            "11.27.0",
            "win32",
            "x64",
            1));
        manifest.Write(Path.Combine(payloadDirectory, RuntimeManifest.FileName));
        var archive = Path.Combine(root, "payload.zip");
        ZipFile.CreateFromDirectory(payloadDirectory, archive, CompressionLevel.NoCompression, includeBaseDirectory: false);
        return (File.ReadAllBytes(archive), payloadDirectory);
    }

    private static byte[] CreateUnsafePayload(string root)
    {
        var archive = Path.Combine(root, "unsafe.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open(), Encoding.UTF8))
            writer.Write("unsafe");
        return File.ReadAllBytes(archive);
    }

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

    private static RouteHandler Routes(RuntimeReleaseDescriptor descriptor, ECDsa key, byte[] payload)
        => new RouteHandler()
            .Add(MetadataUri, () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RuntimeReleaseTrust.Serialize(new SignedRuntimeRelease(
                    descriptor,
                    Convert.ToBase64String(key.SignData(
                        RuntimeReleaseTrust.CreateSigningPayload(descriptor),
                        HashAlgorithmName.SHA256))))),
            })
            .Add(descriptor.PayloadUrl, () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });

    private static Uri MetadataUri { get; } = new("https://updates.example.invalid/runtime.json");

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        internal List<string> RequestedUris { get; } = [];

        internal RouteHandler Add(Uri uri, Func<HttpResponseMessage> response) => Add(uri.AbsoluteUri, response);

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
