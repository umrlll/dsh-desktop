using System.Globalization;
using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record DiagnosticArchiveMaintenanceResult(
    bool Success,
    IReadOnlyList<string> RetainedPaths,
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> SkippedPaths,
    IReadOnlyList<string> Errors);

/// <summary>
/// Conservatively bounds diagnostics emitted by the desktop shell. Only direct-child ZIP files
/// with the exact application-owned timestamped name are eligible; user files and reparse points
/// are retained as evidence and never followed or deleted automatically.
/// </summary>
public static class DiagnosticArchiveRetention
{
    private static readonly Regex ManagedName = new(
        @"^dsh-desktop-diagnostics-(?<stamp>\d{8}-\d{6})\.zip$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static DiagnosticArchiveMaintenanceResult Prune(
        string diagnosticsDirectory,
        int maximumRetained = 5,
        TimeSpan? minimumEvidenceAge = null,
        DateTimeOffset? now = null)
    {
        var retained = new List<string>();
        var removed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var age = minimumEvidenceAge ?? TimeSpan.FromDays(7);
        if (maximumRetained < 0)
        {
            errors.Add("The diagnostic archive retention count cannot be negative.");
            return new DiagnosticArchiveMaintenanceResult(false, retained, removed, skipped, errors);
        }
        if (age < TimeSpan.Zero)
        {
            errors.Add("The diagnostic archive minimum evidence age cannot be negative.");
            return new DiagnosticArchiveMaintenanceResult(false, retained, removed, skipped, errors);
        }

        string root;
        try
        {
            root = Path.GetFullPath(diagnosticsDirectory);
            if (!Directory.Exists(root))
                return new DiagnosticArchiveMaintenanceResult(true, retained, removed, skipped, errors);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(root);
                return new DiagnosticArchiveMaintenanceResult(true, retained, removed, skipped, errors);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            errors.Add(ex.Message);
            return new DiagnosticArchiveMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var candidates = new List<FileInfo>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!ManagedName.IsMatch(name))
                {
                    skipped.Add(path);
                    continue;
                }
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(path);
                    continue;
                }
                candidates.Add(info);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(ex.Message);
            return new DiagnosticArchiveMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var newest = candidates
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();
        var protectedByCount = newest.Take(maximumRetained)
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = (now ?? DateTimeOffset.UtcNow).Subtract(age).UtcDateTime;

        foreach (var file in newest)
        {
            if (protectedByCount.Contains(file.FullName) || file.LastWriteTimeUtc >= cutoff)
            {
                retained.Add(file.FullName);
                continue;
            }
            try
            {
                file.Delete();
                removed.Add(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(ex.Message);
                retained.Add(file.FullName);
            }
        }
        return new DiagnosticArchiveMaintenanceResult(errors.Count == 0, retained, removed, skipped, errors);
    }

    public static bool IsManagedArchiveName(string? name)
        => !string.IsNullOrWhiteSpace(name) && ManagedName.IsMatch(name);
}
