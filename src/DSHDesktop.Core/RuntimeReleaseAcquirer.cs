namespace DSHDesktop.Core;

public enum RuntimeReleaseAcquireStatus
{
    Acquired,
    InvalidRequest,
    DownloadFailed,
    ExtractionFailed,
    ManifestRejected,
    Cancelled,
}

public sealed record RuntimeReleaseSource(
    Uri MetadataUri,
    string Channel,
    IReadOnlyDictionary<string, string> TrustedPublicKeys);

public sealed record RuntimeReleaseAcquireResult(
    RuntimeReleaseAcquireStatus Status,
    RuntimeReleaseDescriptor? Descriptor = null,
    string? RuntimeDirectory = null,
    string? Error = null,
    RuntimeReleaseDownloadStatus? DownloadStatus = null,
    RuntimePayloadExtractionStatus? ExtractionStatus = null)
{
    public bool Success => Status == RuntimeReleaseAcquireStatus.Acquired;
}

/// <summary>
/// Produces a verified, unpacked runtime candidate in a caller-owned staging root. This component
/// deliberately does not activate a slot: callers must apply their own health gate and atomic slot
/// transition after this method succeeds.
/// </summary>
public sealed class RuntimeReleaseAcquirer
{
    private readonly RuntimeReleaseFeedClient _feedClient;
    private readonly RuntimePayloadExtractor _extractor;

    public RuntimeReleaseAcquirer(
        RuntimeReleaseFeedClient feedClient,
        RuntimePayloadExtractor extractor)
    {
        _feedClient = feedClient ?? throw new ArgumentNullException(nameof(feedClient));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    }

    public async Task<RuntimeReleaseAcquireResult> AcquireAsync(
        RuntimeReleaseSource source,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (source.MetadataUri == null
            || string.IsNullOrWhiteSpace(source.Channel)
            || source.TrustedPublicKeys == null
            || source.TrustedPublicKeys.Count == 0)
        {
            return new RuntimeReleaseAcquireResult(
                RuntimeReleaseAcquireStatus.InvalidRequest,
                Error: "A metadata URI, channel, and at least one trusted public key are required.");
        }

        var root = Path.GetFullPath(stagingRoot);
        var nonce = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(root, ".release-" + nonce + ".zip");
        var candidateDirectory = Path.Combine(root, ".runtime-" + nonce);
        var acquired = false;
        try
        {
            Directory.CreateDirectory(root);
            var download = await _feedClient.FetchVerifiedPayloadAsync(
                source.MetadataUri,
                source.TrustedPublicKeys,
                source.Channel,
                archivePath,
                cancellationToken).ConfigureAwait(false);
            if (!download.Success)
            {
                return new RuntimeReleaseAcquireResult(
                    download.Status == RuntimeReleaseDownloadStatus.Cancelled
                        ? RuntimeReleaseAcquireStatus.Cancelled
                        : RuntimeReleaseAcquireStatus.DownloadFailed,
                    download.Descriptor,
                    Error: download.Error,
                    DownloadStatus: download.Status);
            }

            var extraction = await _extractor.ExtractAsync(
                archivePath,
                candidateDirectory,
                cancellationToken).ConfigureAwait(false);
            if (!extraction.Success)
            {
                return new RuntimeReleaseAcquireResult(
                    extraction.Status == RuntimePayloadExtractionStatus.Cancelled
                        ? RuntimeReleaseAcquireStatus.Cancelled
                        : RuntimeReleaseAcquireStatus.ExtractionFailed,
                    download.Descriptor,
                    Error: extraction.Error,
                    DownloadStatus: download.Status,
                    ExtractionStatus: extraction.Status);
            }

            var manifestResult = VerifyRuntimeManifest(candidateDirectory, download.Descriptor!);
            if (manifestResult != null)
            {
                return new RuntimeReleaseAcquireResult(
                    RuntimeReleaseAcquireStatus.ManifestRejected,
                    download.Descriptor,
                    Error: manifestResult,
                    DownloadStatus: download.Status,
                    ExtractionStatus: extraction.Status);
            }

            acquired = true;
            return new RuntimeReleaseAcquireResult(
                RuntimeReleaseAcquireStatus.Acquired,
                download.Descriptor,
                candidateDirectory,
                DownloadStatus: download.Status,
                ExtractionStatus: extraction.Status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RuntimeReleaseAcquireResult(RuntimeReleaseAcquireStatus.Cancelled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new RuntimeReleaseAcquireResult(RuntimeReleaseAcquireStatus.ManifestRejected, Error: ex.Message);
        }
        finally
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { /* best effort */ }
            if (!acquired)
            {
                try
                {
                    if (Directory.Exists(candidateDirectory))
                        Directory.Delete(candidateDirectory, recursive: true);
                }
                catch { /* failed candidates must never be selected as slots */ }
            }
        }
    }

    private static string? VerifyRuntimeManifest(string candidateDirectory, RuntimeReleaseDescriptor descriptor)
    {
        var manifestPath = Path.Combine(candidateDirectory, RuntimeManifest.FileName);
        var manifest = RuntimeManifest.Load(manifestPath);
        var issues = manifest.Verify(candidateDirectory);
        if (issues.Count > 0)
            return "The extracted runtime manifest failed verification: "
                + string.Join(", ", issues.Take(5).Select(issue => issue.Code));
        if (!string.Equals(manifest.RuntimeId, descriptor.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(manifest.DesktopVersion, descriptor.DesktopVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.DshVersion, descriptor.DshVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.NodeVersion, descriptor.NodeVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.PnpmVersion, descriptor.PnpmVersion, StringComparison.Ordinal)
            || !string.Equals(manifest.Platform, descriptor.Platform, StringComparison.Ordinal)
            || !string.Equals(manifest.Architecture, descriptor.Architecture, StringComparison.Ordinal)
            || manifest.ProfileSchema != descriptor.ProfileSchema)
        {
            return "The extracted runtime manifest identity does not match the signed release descriptor.";
        }
        return null;
    }
}
