using System.IO.Compression;

namespace DSHDesktop.Core;

public enum RuntimePayloadExtractionStatus
{
    Extracted,
    InvalidRequest,
    SourceMissing,
    InvalidArchive,
    UnsafeEntry,
    DuplicateEntry,
    ArchiveTooLarge,
    DestinationExists,
    ExtractionFailed,
    Cancelled,
}

public sealed record RuntimePayloadExtractionResult(
    RuntimePayloadExtractionStatus Status,
    string? DestinationDirectory = null,
    string? Error = null)
{
    public bool Success => Status == RuntimePayloadExtractionStatus.Extracted;
}

/// <summary>
/// Extracts a verified runtime archive into an atomically-published directory. Archive paths are
/// validated before any output is written, and entries cannot escape the target directory.
/// Signature and payload-digest verification remain the responsibility of
/// <see cref="RuntimeReleaseFeedClient"/>.
/// </summary>
public sealed class RuntimePayloadExtractor
{
    public int MaximumEntryCount { get; init; } = 50_000;
    public long MaximumExpandedBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    public async Task<RuntimePayloadExtractionResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (MaximumEntryCount <= 0 || MaximumExpandedBytes <= 0)
            return new RuntimePayloadExtractionResult(
                RuntimePayloadExtractionStatus.InvalidRequest,
                Error: "Extraction limits must be positive.");

        var sourcePath = Path.GetFullPath(archivePath);
        var finalDirectory = Path.GetFullPath(destinationDirectory);
        if (!File.Exists(sourcePath))
            return new RuntimePayloadExtractionResult(
                RuntimePayloadExtractionStatus.SourceMissing,
                Error: "The runtime payload archive does not exist.");
        if (Directory.Exists(finalDirectory) || File.Exists(finalDirectory))
            return new RuntimePayloadExtractionResult(
                RuntimePayloadExtractionStatus.DestinationExists,
                Error: "The destination already exists and will not be overwritten.");

        var parent = Path.GetDirectoryName(finalDirectory);
        if (string.IsNullOrWhiteSpace(parent))
            return new RuntimePayloadExtractionResult(
                RuntimePayloadExtractionStatus.InvalidRequest,
                Error: "The destination must have a parent directory.");

        var temporaryDirectory = finalDirectory + ".extract-" + Guid.NewGuid().ToString("N");
        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            var entries = ValidateEntries(archive, out var validation);
            if (validation != null) return validation;

            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(temporaryDirectory);
            long extractedBytes = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsDirectoryEntry(entry))
                {
                    Directory.CreateDirectory(GetDestinationPath(temporaryDirectory, entry.FullName));
                    continue;
                }

                var destination = GetDestinationPath(temporaryDirectory, entry.FullName);
                var directory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("Archive entry does not have a destination directory.");
                Directory.CreateDirectory(directory);
                await using var input = entry.Open();
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                extractedBytes += await CopyWithinLimitAsync(
                    input,
                    output,
                    MaximumExpandedBytes - extractedBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            Directory.Move(temporaryDirectory, finalDirectory);
            return new RuntimePayloadExtractionResult(
                RuntimePayloadExtractionStatus.Extracted,
                finalDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RuntimePayloadExtractionResult(RuntimePayloadExtractionStatus.Cancelled);
        }
        catch (ArchiveLimitExceededException ex)
        {
            return new RuntimePayloadExtractionResult(RuntimePayloadExtractionStatus.ArchiveTooLarge, Error: ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return new RuntimePayloadExtractionResult(RuntimePayloadExtractionStatus.InvalidArchive, Error: ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RuntimePayloadExtractionResult(RuntimePayloadExtractionStatus.ExtractionFailed, Error: ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
                // Best effort: a failed extraction never publishes this directory.
            }
        }
    }

    private IReadOnlyList<ZipArchiveEntry> ValidateEntries(
        ZipArchive archive,
        out RuntimePayloadExtractionResult? validation)
    {
        validation = null;
        if (archive.Entries.Count > MaximumEntryCount)
        {
            validation = new RuntimePayloadExtractionResult(
                RuntimePayloadExtractionStatus.ArchiveTooLarge,
                Error: "The archive has too many entries.");
            return [];
        }

        long expandedBytes = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!TryGetSafeRelativePath(entry.FullName, out var relativePath))
            {
                validation = new RuntimePayloadExtractionResult(
                    RuntimePayloadExtractionStatus.UnsafeEntry,
                    Error: "The archive contains an unsafe entry path.");
                return [];
            }
            if (!paths.Add(relativePath))
            {
                validation = new RuntimePayloadExtractionResult(
                    RuntimePayloadExtractionStatus.DuplicateEntry,
                    Error: "The archive contains duplicate entry paths.");
                return [];
            }
            if (IsDirectoryEntry(entry)) continue;

            try { expandedBytes = checked(expandedBytes + entry.Length); }
            catch (OverflowException)
            {
                validation = new RuntimePayloadExtractionResult(
                    RuntimePayloadExtractionStatus.ArchiveTooLarge,
                    Error: "The archive expanded size overflows the configured limit.");
                return [];
            }
            if (expandedBytes > MaximumExpandedBytes)
            {
                validation = new RuntimePayloadExtractionResult(
                    RuntimePayloadExtractionStatus.ArchiveTooLarge,
                    Error: "The archive expanded size exceeds the configured limit.");
                return [];
            }
        }

        return archive.Entries;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
        => entry.FullName.EndsWith("/", StringComparison.Ordinal);

    private static async Task<long> CopyWithinLimitAsync(
        Stream input,
        Stream output,
        long remainingMaximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return copied;
            copied += read;
            if (copied > remainingMaximumBytes)
                throw new ArchiveLimitExceededException();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetDestinationPath(string root, string entryPath)
    {
        if (!TryGetSafeRelativePath(entryPath, out var relativePath))
            throw new InvalidDataException("The archive contains an unsafe entry path.");
        var destination = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The archive entry escapes its extraction directory.");
        return destination;
    }

    private static bool TryGetSafeRelativePath(string entryPath, out string relativePath)
    {
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(entryPath)
            || entryPath.StartsWith("/", StringComparison.Ordinal)
            || entryPath.Contains('\\')
            || entryPath.IndexOfAny(['\0', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            return false;

        var trimmed = entryPath.TrimEnd('/');
        if (trimmed.Length == 0 || trimmed.Contains("//", StringComparison.Ordinal)) return false;
        var segments = trimmed.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or "..")) return false;
        relativePath = string.Join('/', segments);
        return true;
    }

    private sealed class ArchiveLimitExceededException : Exception
    {
        internal ArchiveLimitExceededException()
            : base("The archive expanded size exceeds the configured limit.") { }
    }
}
