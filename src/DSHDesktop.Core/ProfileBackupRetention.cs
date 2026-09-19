using System.Text.RegularExpressions;

namespace DSHDesktop.Core;

public sealed record ProfileBackupMaintenanceResult(
    bool Success,
    IReadOnlyList<string> RetainedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<string> SkippedFiles,
    IReadOnlyList<string> Errors);

/// <summary>
/// Conservatively bounds backups created alongside profile manifest files. Only direct children
/// whose names exactly match an approved manifest plus <c>.bak-yyyyMMdd-HHmmss</c> are eligible;
/// user files and reparse points are never removed automatically.
/// </summary>
public static class ProfileBackupRetention
{
    private static readonly Regex BackupTimestamp = new(
        @"^\d{8}-\d{6}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ProfileBackupMaintenanceResult Prune(
        string profileDirectory,
        IReadOnlyCollection<string> manifestFileNames,
        int maximumRetainedPerManifest = 5,
        TimeSpan? minimumEvidenceAge = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentNullException.ThrowIfNull(manifestFileNames);
        var retained = new List<string>();
        var removed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var age = minimumEvidenceAge ?? TimeSpan.FromDays(7);
        if (maximumRetainedPerManifest < 0)
        {
            errors.Add("The profile backup retention count cannot be negative.");
            return new ProfileBackupMaintenanceResult(false, retained, removed, skipped, errors);
        }
        if (age < TimeSpan.Zero)
        {
            errors.Add("The profile backup evidence age cannot be negative.");
            return new ProfileBackupMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var root = Path.GetFullPath(profileDirectory);
        if (!Directory.Exists(root))
            return new ProfileBackupMaintenanceResult(true, retained, removed, skipped, errors);
        var approved = new HashSet<string>(manifestFileNames.Where(IsSafeFileName), StringComparer.Ordinal);
        if (approved.Count != manifestFileNames.Count)
        {
            errors.Add("Profile backup manifest names must be simple file names.");
            return new ProfileBackupMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var grouped = approved.ToDictionary(name => name, _ => new List<FileInfo>(), StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFiles(root))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(path);
                    continue;
                }
                foreach (var manifest in approved)
                {
                    var prefix = manifest + ".bak-";
                    if (!info.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var suffix = info.Name[prefix.Length..];
                    if (BackupTimestamp.IsMatch(suffix)) grouped[manifest].Add(info);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add("Unable to enumerate profile backups: " + ex.Message);
            return new ProfileBackupMaintenanceResult(false, retained, removed, skipped, errors);
        }

        var cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - age;
        foreach (var files in grouped.Values)
        {
            foreach (var backup in files.OrderByDescending(file => file.LastWriteTimeUtc)
                         .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                         .Select((file, index) => (file, index)))
            {
                if (backup.index < maximumRetainedPerManifest || backup.file.LastWriteTimeUtc > cutoff)
                {
                    retained.Add(backup.file.FullName);
                    continue;
                }
                try
                {
                    backup.file.Delete();
                    removed.Add(backup.file.FullName);
                }
                catch (Exception ex)
                {
                    errors.Add("Unable to remove profile backup " + backup.file.Name + ": " + ex.Message);
                }
            }
        }

        return new ProfileBackupMaintenanceResult(errors.Count == 0, retained, removed, skipped, errors);
    }

    private static bool IsSafeFileName(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
}
