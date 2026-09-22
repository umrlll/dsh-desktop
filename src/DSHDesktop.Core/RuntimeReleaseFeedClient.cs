using System.Net.Http;
using System.Text.Json;

namespace DSHDesktop.Core;

public enum RuntimeReleaseDownloadStatus
{
    Downloaded,
    InvalidRequest,
    MetadataDownloadFailed,
    MetadataTooLarge,
    MetadataInvalid,
    MetadataRejected,
    ChannelMismatch,
    PayloadDownloadFailed,
    PayloadTooLarge,
    PayloadIntegrityFailed,
    DestinationExists,
    Cancelled,
}

public sealed record RuntimeReleaseDownloadResult(
    RuntimeReleaseDownloadStatus Status,
    RuntimeReleaseDescriptor? Descriptor = null,
    string? PayloadPath = null,
    string? Error = null)
{
    public bool Success => Status == RuntimeReleaseDownloadStatus.Downloaded;
}

public enum RuntimeReleaseMetadataStatus
{
    Verified,
    InvalidRequest,
    DownloadFailed,
    TooLarge,
    Invalid,
    Rejected,
    ChannelMismatch,
    Cancelled,
}

public sealed record RuntimeReleaseMetadataResult(
    RuntimeReleaseMetadataStatus Status,
    RuntimeReleaseDescriptor? Descriptor = null,
    string? Error = null)
{
    public bool Success => Status == RuntimeReleaseMetadataStatus.Verified;
}

/// <summary>
/// Fetches a signed release document and writes its verified payload atomically. No bytes are
/// published at <paramref name="destinationPath"/> until the document signature, channel, size,
/// and payload SHA-256 have all passed. The caller owns unpacking the resulting file into a
/// separate inactive runtime staging directory.
/// </summary>
public sealed class RuntimeReleaseFeedClient
{
    private readonly HttpClient _http;

    public RuntimeReleaseFeedClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public long MaximumMetadataBytes { get; init; } = 64 * 1024;
    public long MaximumPayloadBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Obtains and verifies just the signed release descriptor. This is the safe update-check
    /// primitive: callers can present a candidate only after its channel and trust root pass,
    /// without downloading a runtime payload.
    /// </summary>
    public async Task<RuntimeReleaseMetadataResult> FetchVerifiedDescriptorAsync(
        Uri metadataUri,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string requiredChannel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadataUri);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredChannel);
        if (!metadataUri.IsAbsoluteUri
            || !string.Equals(metadataUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || MaximumMetadataBytes <= 0)
        {
            return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.InvalidRequest);
        }

        SignedRuntimeRelease release;
        try
        {
            using var response = await _http.GetAsync(
                metadataUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length > MaximumMetadataBytes)
                return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.TooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            release = RuntimeReleaseTrust.Parse(await ReadTextWithinLimitAsync(
                stream,
                MaximumMetadataBytes,
                cancellationToken).ConfigureAwait(false));
        }
        catch (PayloadTooLargeException ex)
        {
            return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.TooLarge, Error: ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.Cancelled);
        }
        catch (JsonException ex)
        {
            return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.Invalid, Error: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.DownloadFailed, Error: ex.Message);
        }

        var verification = RuntimeReleaseTrust.Verify(release, trustedPublicKeys);
        if (!verification.IsVerified)
            return new RuntimeReleaseMetadataResult(
                RuntimeReleaseMetadataStatus.Rejected,
                release.Descriptor,
                verification.Status + ": " + verification.Error);
        if (!string.Equals(release.Descriptor.Channel, requiredChannel, StringComparison.Ordinal))
            return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.ChannelMismatch, release.Descriptor);
        return new RuntimeReleaseMetadataResult(RuntimeReleaseMetadataStatus.Verified, release.Descriptor);
    }

    public async Task<RuntimeReleaseDownloadResult> FetchVerifiedPayloadAsync(
        Uri metadataUri,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        string requiredChannel,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadataUri);
        ArgumentNullException.ThrowIfNull(trustedPublicKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredChannel);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!metadataUri.IsAbsoluteUri
            || !string.Equals(metadataUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || MaximumMetadataBytes <= 0
            || MaximumPayloadBytes <= 0)
        {
            return new RuntimeReleaseDownloadResult(
                RuntimeReleaseDownloadStatus.InvalidRequest,
                Error: "release 元数据地址必须为 HTTPS，且下载大小限制必须为正数。");
        }

        SignedRuntimeRelease release;
        try
        {
            using var response = await _http.GetAsync(
                metadataUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var declaredMetadataLength = response.Content.Headers.ContentLength;
            if (declaredMetadataLength.HasValue && declaredMetadataLength.Value > MaximumMetadataBytes)
            {
                return new RuntimeReleaseDownloadResult(
                    RuntimeReleaseDownloadStatus.MetadataTooLarge,
                    Error: "release 元数据超过大小限制。");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            release = RuntimeReleaseTrust.Parse(await ReadTextWithinLimitAsync(
                stream,
                MaximumMetadataBytes,
                cancellationToken).ConfigureAwait(false));
        }
        catch (PayloadTooLargeException ex)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.MetadataTooLarge, Error: ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.Cancelled);
        }
        catch (JsonException ex)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.MetadataInvalid, Error: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.MetadataDownloadFailed, Error: ex.Message);
        }

        var verification = RuntimeReleaseTrust.Verify(release, trustedPublicKeys);
        if (!verification.IsVerified)
        {
            return new RuntimeReleaseDownloadResult(
                RuntimeReleaseDownloadStatus.MetadataRejected,
                release.Descriptor,
                Error: verification.Status + ": " + verification.Error);
        }
        if (!string.Equals(release.Descriptor.Channel, requiredChannel, StringComparison.Ordinal))
        {
            return new RuntimeReleaseDownloadResult(
                RuntimeReleaseDownloadStatus.ChannelMismatch,
                release.Descriptor,
                Error: "release channel 与请求通道不一致。");
        }

        var finalPath = Path.GetFullPath(destinationPath);
        if (File.Exists(finalPath))
        {
            return new RuntimeReleaseDownloadResult(
                RuntimeReleaseDownloadStatus.DestinationExists,
                release.Descriptor,
                Error: "目标载荷文件已存在，拒绝覆盖。");
        }

        var parent = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrWhiteSpace(parent))
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.InvalidRequest, release.Descriptor,
                Error: "目标载荷路径缺少父目录。");
        var temporaryPath = finalPath + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(parent);
            using var response = await _http.GetAsync(
                release.Descriptor.PayloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var declaredPayloadLength = response.Content.Headers.ContentLength;
            if (declaredPayloadLength.HasValue && declaredPayloadLength.Value > MaximumPayloadBytes)
            {
                return new RuntimeReleaseDownloadResult(
                    RuntimeReleaseDownloadStatus.PayloadTooLarge,
                    release.Descriptor,
                    Error: "release 载荷超过大小限制。");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await CopyWithinLimitAsync(input, output, MaximumPayloadBytes, cancellationToken).ConfigureAwait(false);
            }

            await using (var payload = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: false))
            {
                if (!RuntimeReleaseTrust.VerifyPayload(payload, release.Descriptor))
                {
                    return new RuntimeReleaseDownloadResult(
                        RuntimeReleaseDownloadStatus.PayloadIntegrityFailed,
                        release.Descriptor,
                        Error: "release 载荷 SHA-256 与已验签元数据不一致。");
                }
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
            return new RuntimeReleaseDownloadResult(
                RuntimeReleaseDownloadStatus.Downloaded,
                release.Descriptor,
                finalPath);
        }
        catch (PayloadTooLargeException ex)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.PayloadTooLarge, release.Descriptor, Error: ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.Cancelled, release.Descriptor);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return new RuntimeReleaseDownloadResult(RuntimeReleaseDownloadStatus.PayloadDownloadFailed, release.Descriptor, Error: ex.Message);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* best effort */ }
        }
    }

    private static async Task<string> ReadTextWithinLimitAsync(
        Stream input,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await CopyWithinLimitAsync(input, output, maximumBytes, cancellationToken).ConfigureAwait(false);
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static async Task CopyWithinLimitAsync(
        Stream input,
        Stream output,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return;
            total += read;
            if (total > maximumBytes) throw new PayloadTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PayloadTooLargeException : Exception
    {
        internal PayloadTooLargeException() : base("下载内容超过配置大小限制。") { }
    }
}
