using System.IO.Compression;

namespace DSHDesktop.Core;

public enum PortableReleaseArchiveStatus
{
    Created,
    InvalidRequest,
    ShellIncomplete,
    RuntimeNotReady,
    UnsafePath,
    Failed,
}

public sealed record PortableReleaseArchiveResult(
    PortableReleaseArchiveStatus Status,
    string? OutputPath = null,
    int FileCount = 0,
    string? Error = null)
{
    public bool Success => Status == PortableReleaseArchiveStatus.Created;
}

/// <summary>
/// Creates a deterministic Portable ZIP only from a complete release root. The active runtime
/// slot is fully verified before archiving; the archive never follows reparse points or includes
/// its own output file, so a shell-only CI publish cannot be presented as a Portable build.
/// </summary>
public static class PortableReleaseArchive
{
    private static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static PortableReleaseArchiveResult Create(string publishRoot, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(publishRoot) || string.IsNullOrWhiteSpace(outputPath))
            return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.InvalidRequest, Error: "Publish root and output path are required.");
        try
        {
            var root = Path.GetFullPath(publishRoot);
            var output = Path.GetFullPath(outputPath);
            if (!Directory.Exists(root))
                return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.InvalidRequest, Error: "Publish root does not exist.");
            if (IsWithinRoot(root, output))
                return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.InvalidRequest, Error: "Portable output must be outside the publish root.");
            if (!File.Exists(Path.Combine(root, "DSHDesktop.exe"))
                || !File.Exists(Path.Combine(root, "DSHDesktop.dll")))
            {
                return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.ShellIncomplete, Error: "Publish root is missing the desktop shell.");
            }

            var runtime = new RuntimeSlotManager(Path.Combine(root, "runtime")).ResolveActive(verifyFiles: true);
            if (!runtime.IsReady)
            {
                return new PortableReleaseArchiveResult(
                    PortableReleaseArchiveStatus.RuntimeNotReady,
                    Error: runtime.Error ?? runtime.Status.ToString());
            }

            if (!TryCollectFiles(root, out var files, out var error))
                return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.UnsafePath, Error: error);

            var parent = Path.GetDirectoryName(output)
                ?? throw new InvalidDataException("Portable output path has no parent directory.");
            Directory.CreateDirectory(parent);
            var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
                {
                    foreach (var file in files)
                    {
                        var entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Optimal);
                        entry.LastWriteTime = ZipTimestamp;
                        using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var destination = entry.Open();
                        input.CopyTo(destination);
                    }
                }
                File.Move(temporary, output, overwrite: true);
                return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.Created, output, files.Count);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new PortableReleaseArchiveResult(PortableReleaseArchiveStatus.Failed, Error: ex.Message);
        }
    }

    private static bool TryCollectFiles(string root, out List<(string FullPath, string RelativePath)> files, out string? error)
    {
        files = [];
        error = null;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                error = "Publish root contains a reparse-point directory.";
                return false;
            }
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Publish root contains a reparse-point entry.";
                    return false;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!IsSafeRelativePath(relative))
                {
                    error = "Publish root contains an unsafe relative path.";
                    return false;
                }
                files.Add((path, relative));
            }
        }
        files = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList();
        if (files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count() != files.Count)
        {
            error = "Publish root contains duplicate archive paths.";
            return false;
        }
        return true;
    }

    private static bool IsSafeRelativePath(string path)
        => !string.IsNullOrWhiteSpace(path)
           && !Path.IsPathRooted(path)
           && path.Split('/').All(segment => segment is not "" and not "." and not "..");

    private static bool IsWithinRoot(string root, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
